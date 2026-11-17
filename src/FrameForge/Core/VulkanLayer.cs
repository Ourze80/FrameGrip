using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace FrameGrip.Core;

/// <summary>
/// Registra la DLL di FrameGrip come "layer implicito" di Vulkan per l'utente corrente:
/// è il modo ufficiale con cui un overlay entra nei giochi Vulkan (lo usano anche Steam e OBS).
/// La registrazione viene tolta alla chiusura dell'app; se restasse, il layer è comunque
/// inattivo finché l'app non gira, e si disattiva con la variabile DISABLE_FRAMEGRIP_VK_LAYER=1.
/// </summary>
public static class VulkanLayer
{
    private const string KeyPath = @"Software\Khronos\Vulkan\ImplicitLayers";

    private static string ManifestDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameGrip");

    private static string ManifestPath(string bits) => Path.Combine(ManifestDir, $"FrameGripVulkan{bits}.json");

    /// <summary>Null se riuscito, altrimenti l'errore (tradotto quando lo si mostra).</summary>
    public static LocText? Register()
    {
        try
        {
            Directory.CreateDirectory(ManifestDir);
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            foreach (string bits in new[] { "64", "32" })
            {
                string dll = Path.Combine(AppContext.BaseDirectory, $"FrameGripHook{bits}.dll");
                if (!File.Exists(dll)) continue;

                var manifest = new
                {
                    file_format_version = "1.2.1",
                    layer = new
                    {
                        name = $"VK_LAYER_FRAMEGRIP_overlay_{bits}",
                        type = "GLOBAL",
                        library_path = dll,
                        library_arch = bits,
                        api_version = "1.3.0",
                        implementation_version = "1",
                        description = "FrameGrip: frame limiter, frame measurement and overlay",
                        functions = new Dictionary<string, string>
                        {
                            ["vkNegotiateLoaderLayerInterfaceVersion"] = "vkNegotiateLoaderLayerInterfaceVersion",
                        },
                        disable_environment = new Dictionary<string, string> { ["DISABLE_FRAMEGRIP_VK_LAYER"] = "1" },
                    },
                };
                File.WriteAllText(ManifestPath(bits), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                key.SetValue(ManifestPath(bits), 0, RegistryValueKind.DWord);
            }
            return null;
        }
        catch (Exception ex)
        {
            return new LocText("Vulkan.Failed", ex.Message);
        }
    }

    public static void Unregister()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            foreach (string bits in new[] { "64", "32" })
            {
                key?.DeleteValue(ManifestPath(bits), throwOnMissingValue: false);
                File.Delete(ManifestPath(bits));
            }
        }
        catch { /* alla peggio resta un layer inattivo */ }
    }
}
