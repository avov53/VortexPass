namespace VortexPass.Core.Services;

public static class DnsResponsePolicy
{
    public static bool HasUsableAnswer(ReadOnlySpan<byte> response) =>
        response.Length >= 12 &&
        (response[3] & 0x0f) == 0 &&
        ((response[6] << 8) | response[7]) > 0;

    public static bool IsNegative(ReadOnlySpan<byte> response) =>
        response.Length < 8 ||
        (response[3] & 0x0f) != 0 ||
        response[6] == 0 && response[7] == 0;
}
