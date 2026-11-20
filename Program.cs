using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:9100");

builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p =>
        p.AllowAnyHeader()
         .AllowAnyMethod()
         .SetIsOriginAllowed(_ => true)); // solo localhost en la práctica
});

var app = builder.Build();
app.UseCors();

var printerName = builder.Configuration["PrinterName"] ?? "POS-58";

app.MapGet("/health", () => Results.Ok(new { ok = true, printer = printerName }));

app.MapPost("/print", async (HttpRequest req) =>
{
    TicketDto? ticket;
    try
    {
        ticket = await req.ReadFromJsonAsync<TicketDto>();
    }
    catch
    {
        return Results.BadRequest(new { message = "JSON inválido" });
    }

    if (ticket is null || string.IsNullOrWhiteSpace(ticket.Folio))
        return Results.BadRequest(new { message = "Falta folio" });

    try
    {
        var text = BuildTicketText(ticket);
        RawPrinterHelper.SendStringToPrinter(printerName, text);
        return Results.Ok(new { ok = true, folio = ticket.Folio, printer = printerName });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, message = ex.Message }, statusCode: 500);
    }
});
app.MapGet("/printers", () =>
{
    var names = System.Drawing.Printing.PrinterSettings.InstalledPrinters
        .Cast<string>()
        .ToList();
    return Results.Ok(names);
});

app.Run();



static string BuildTicketText(TicketDto t)
{
    const int W = 32;
    var sb = new StringBuilder();

    // Init
    sb.Append((char)0x1B).Append('@');           // ESC @  reset
    sb.Append((char)0x1B).Append('a').Append((char)1); // centrar

    // Título marca (doble alto + negrita)
    sb.Append((char)0x1B).Append('E').Append((char)1); // negrita ON
    sb.Append((char)0x1D); // doble alto+ancho
    sb.Append(" RAUDAL - SINERGYFOOD\n");
    sb.Append((char)0x1D).Append('!').Append((char)0x00); // tamaño normal
    sb.Append((char)0x1B).Append('E').Append((char)0); // negrita OFF
    sb.Append("Comedor industrial\n");

    sb.Append((char)0x1B).Append('a').Append((char)0); // izquierda
    sb.Append(new string('-', W)).Append('\n');

    // Título del vale centrado + negrita
    sb.Append((char)0x1B).Append('a').Append((char)1);
    sb.Append((char)0x1B).Append('E').Append((char)1);
    sb.Append((t.Title ?? "COMEDOR").ToUpperInvariant()).Append('\n');
    sb.Append((char)0x1B).Append('E').Append((char)0);
    sb.Append((char)0x1B).Append('a').Append((char)0);
    sb.Append(new string('-', W)).Append('\n');

    sb.Append(Line("Empleado", t.EmployeeNumber, W)).Append('\n');
    sb.Append(Line("Nombre", t.DinerName, W)).Append('\n');
    sb.Append(Line("Comida", t.MealTypeName, W)).Append('\n');
    sb.Append(Line("Fecha", t.IssuedAtLabel, W)).Append('\n');
    sb.Append(Line("Valido", t.ValidFor ?? "1 consumo", W)).Append('\n');

    sb.Append(new string('-', W)).Append('\n');

    // Folio centrado negrita
    sb.Append((char)0x1B).Append('a').Append((char)1);
    sb.Append((char)0x1B).Append('E').Append((char)1);
    sb.Append(t.Folio ?? "").Append('\n');
    sb.Append((char)0x1B).Append('E').Append((char)0);
    sb.Append(new string('-', W)).Append('\n');
    sb.Append("Conserve este ticket\n");
    sb.Append((char)0x1B).Append('a').Append((char)0);

    sb.Append("\n\n\n");

    // Corte parcial (si tu modelo lo soporta)
    sb.Append((char)0x1D).Append('V').Append((char)1);

    return sb.ToString();
}

static string Line(string label, string? value, int width)
{
    label ??= "";
    value = (value ?? "").Trim();
    var space = width - label.Length - 1;
    if (space < 1) space = 1;
    if (value.Length > space) value = value[..space];
    return label + new string(' ', Math.Max(1, space - value.Length)) + value;
}

static class EscPosLogo
{
    /// <summary>Genera bytes ESC/POS (GS v 0) a partir de un PNG.</summary>
    public static byte[]? BuildRaster(string path, int maxWidthDots = 384)
    {
        if (!File.Exists(path)) return null;

        using var original = new Bitmap(path);
        var w = original.Width;
        var h = original.Height;
        if (w > maxWidthDots)
        {
            h = (int)(h * (maxWidthDots / (double)w));
            w = maxWidthDots;
        }

        // ancho múltiplo de 8
        w = (w + 7) / 8 * 8;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.DrawImage(original, 0, 0, w, h);
        }

        var widthBytes = w / 8;
        var data = new byte[widthBytes * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                // luminosidad; umbral para blanco/negro
                var lum = (c.R * 0.3 + c.G * 0.59 + c.B * 0.11);
                var black = lum < 128 && c.A > 64;
                if (black)
                    data[y * widthBytes + (x / 8)] |= (byte)(0x80 >> (x % 8));
            }
        }

        // GS v 0 m xL xH yL yH data
        var xL = (byte)(widthBytes & 0xFF);
        var xH = (byte)((widthBytes >> 8) & 0xFF);
        var yL = (byte)(h & 0xFF);
        var yH = (byte)((h >> 8) & 0xFF);

        using var ms = new MemoryStream();
        ms.WriteByte(0x1D); // GS
        ms.WriteByte((byte)'v');
        ms.WriteByte((byte)'0');
        ms.WriteByte(0x00); // m = normal
        ms.WriteByte(xL);
        ms.WriteByte(xH);
        ms.WriteByte(yL);
        ms.WriteByte(yH);
        ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }
}

record TicketDto(
    string? Title,
    string? EmployeeNumber,
    string? DinerName,
    string? MealTypeName,
    string? IssuedAtLabel,
    string? ValidFor,
    string? Folio
);

/// <summary>Envío RAW a impresora de Windows (winspool).</summary>
static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public class DOCINFOA
    {
        [MarshalAs(UnmanagedType.LPStr)] public string pDocName = "InsumoPro Ticket";
        [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPStr)] public string pDataType = "RAW";
    }

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern bool OpenPrinter(string szPrinter, out IntPtr hPrinter, IntPtr pd);

    [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOA di);

    [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true)]
    static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static void SendStringToPrinter(string printerName, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text); // code page típica tickets
        var pBytes = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pBytes, bytes.Length);
            if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
                throw new Exception($"No se pudo abrir la impresora '{printerName}'. Revisa el nombre en Windows.");

            try
            {
                var di = new DOCINFOA();
                if (!StartDocPrinter(hPrinter, 1, di))
                    throw new Exception("StartDocPrinter falló");

                try
                {
                    if (!StartPagePrinter(hPrinter))
                        throw new Exception("StartPagePrinter falló");

                    try
                    {
                        if (!WritePrinter(hPrinter, pBytes, bytes.Length, out _))
                            throw new Exception("WritePrinter falló");
                    }
                    finally
                    {
                        EndPagePrinter(hPrinter);
                    }
                }
                finally
                {
                    EndDocPrinter(hPrinter);
                }
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pBytes);
        }
    }
}