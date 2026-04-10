using QRCoder;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: QrGen <output-path> <payload-json>");
    return 1;
}

var outputPath = args[0];
var payloadJson = args[1];

using var generator = new QRCodeGenerator();
using var data = generator.CreateQrCode(payloadJson, QRCodeGenerator.ECCLevel.Q);
var qrCode = new PngByteQRCode(data);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await File.WriteAllBytesAsync(outputPath, qrCode.GetGraphic(20));
return 0;
