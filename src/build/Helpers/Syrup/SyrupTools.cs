using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Deneblab.AbcVersion;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Serilog;

namespace Helpers.Syrup;

[PublicAPI]
public static class SyrupTools
{
    public static void MakeSyrupFile(string path, AbcVersion magicVersion, string appName)
    {
        var r = new SyrupInfo();
        r.App = appName;
        r.Name = Path.GetFileNameWithoutExtension(path);
        r.File = Path.GetFileName(path);
        r.Sha = GetsShaHashForFile(path);
        r.SemVer = magicVersion.SemVersion;
        r.Channel = magicVersion.GitBranch;
        r.ReleaseDate = $"{magicVersion.DateTime:s}Z";
        var dst = Path.GetFullPath(path) + ".syrup";
        var json = JsonConvert.SerializeObject(r, Formatting.Indented);
        File.WriteAllText(dst, json, new UTF8Encoding(false));
        Log.Information($"Syrup; Make syrup for: {path}; Syrup file: {Path.GetFileName(dst)}");
    }

    static string GetsShaHashForFile(string path)
    {
        using (var stream = new BufferedStream(File.OpenRead(path)))
        {
            using (var sha = SHA256.Create())
            {
                var checksum = sha.ComputeHash(stream);
                return BitConverter.ToString(checksum).Replace("-", string.Empty).ToLower();
            }
        }
    }
}