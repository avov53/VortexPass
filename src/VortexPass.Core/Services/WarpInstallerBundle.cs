using System.Security.Cryptography;

namespace VortexPass.Core.Services;

public static class WarpInstallerBundle
{
    public const string FileName = "Cloudflare_WARP_Release-x64.msi";
    public const string Sha256 = "ab3138dbebdf132af076b5ebcb25b42345d29258acb76267c3bd1f8cfea57a53";
    public static async Task<string> VerifyTransformAsync(string installer, CancellationToken token=default)
    {
        await using(var msi=File.OpenRead(installer))
            if(!Convert.ToHexString(await SHA256.HashDataAsync(msi,token)).Equals(Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Версия установщика WARP не соответствует проверенному MSI-transform");
        var path=Path.Combine(Path.GetDirectoryName(installer)!,"suppress-gui.mst");
        await using var expected=typeof(WarpInstallerBundle).Assembly.GetManifestResourceStream("VortexPass.WarpGuiTransform")??throw new InvalidDataException("Не найден эталон MSI-transform WARP");
        await using var actual=File.OpenRead(path);
        var expectedHash=await SHA256.HashDataAsync(expected,token);
        var actualHash=await SHA256.HashDataAsync(actual,token);
        if(!CryptographicOperations.FixedTimeEquals(expectedHash,actualHash))
            throw new InvalidDataException("MSI-transform WARP повреждён или заменён; установка остановлена");
        return path;
    }

    public static async Task<string?> FindAsync(string applicationDirectory, CancellationToken token = default)
    {
        var path=Path.Combine(applicationDirectory,"warp-installer",FileName);
        if(!File.Exists(path))return null;
        await using(var stream=File.OpenRead(path))
        {
            var hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,token));
            if(!hash.Equals(Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Встроенный установщик WARP повреждён или заменён. Запустите программу из полного исходного комплекта.");
        }
        if(!WarpService.IsTrustedInstaller(path,out var signer))
            throw new InvalidDataException($"Не подтверждена цифровая подпись встроенного установщика Cloudflare ({signer}).");
        return path;
    }
}
