using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using VortexPass.Core.Services;

namespace VortexPass.App;
public partial class MainWindow
{
    private async Task TransferProfileAsync(JsonElement root)
    {
        var requestId = root.GetProperty("requestId").GetString();
        try
        {
            var type = root.GetProperty("type").GetString();
            if (type == "connections.export")
            {
                var profile = GetConnection(root.GetProperty("id").GetString());
                var json = ProfileTransferService.Export(profile);
                if (Encoding.UTF8.GetByteCount(json) > ProfileTransferService.MaxBytes) throw new ArgumentException("Размер профиля превышает 64 КБ.");
                var link = ProfileTransferService.Link(json);
                var action = root.GetProperty("action").GetString();
                var detail = "";
                if (action == "file")
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog { Filter="Профиль VortexPass|*.vortexpass.json", DefaultExt=".vortexpass.json",
                        FileName = string.Concat(profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".vortexpass.json" };
                    if (dialog.ShowDialog(this) != true) { await PostUiAsync(new {type="connections.transfer.status", requestId, state="cancelled"}); return; }
                    await File.WriteAllTextAsync(dialog.FileName,json,new UTF8Encoding(false)); detail="Файл профиля сохранён.";
                }
                else if (action == "copy")
                {
                    try { System.Windows.Clipboard.SetText(link); detail="Код-ссылка скопирована."; }
                    catch (System.Runtime.InteropServices.ExternalException) { detail="Буфер обмена занят. Скопируйте код-ссылку ниже."; }
                }
                await PostUiAsync(new {type="connections.transfer.status", requestId, state="exported", link, detail});
                return;
            }
            var text = root.GetProperty("text").GetString() ?? "";
            var imported = ProfileTransferService.Parse(text);
            if (type == "connections.import.preview")
            {
                await PostUiAsync(new {type="connections.transfer.status", requestId, state="preview", profile=ConnectionDto(imported)});
                return;
            }
            var originalName = imported.Name; var suffix = 1;
            while (_settings.SavedConnections.Any(p=>p.Name.Equals(imported.Name,StringComparison.OrdinalIgnoreCase)))
                imported.Name = originalName[..Math.Min(60,originalName.Length)] + $" (копия {suffix++})";
            using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { profile=ConnectionDto(imported), imported=true }));
            await SaveConnectionAsync(payload.RootElement);
            await PostUiAsync(new {type="connections.transfer.status", requestId, state="imported", detail="Профиль добавлен. Маршрут требует проверки в вашей сети."});
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { await PostUiAsync(new {type="connections.transfer.status", requestId, state="failed", error=ex.Message}); }
    }
}
