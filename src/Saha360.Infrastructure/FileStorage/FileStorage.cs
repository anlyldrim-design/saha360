using System.Text.RegularExpressions;

namespace Saha360.Infrastructure.FileStorage;

public interface IFileStorage
{
    Task SaveAsync(string storageKey, Stream content, CancellationToken ct = default);
    Stream? OpenRead(string storageKey);
    void Delete(string storageKey);
}

/// <summary>
/// Dosyalar web kökünün DIŞINDA, rastgele adla saklanır. Kullanıcının verdiği dosya adı asla yol olarak kullanılmaz;
/// dosyalar yalnız yetki kontrolü yapan bir denetleyici üzerinden sunulur.
/// </summary>
public partial class LocalFileStorage(string rootPath) : IFileStorage
{
    public string RootPath { get; } = Path.GetFullPath(rootPath);

    [GeneratedRegex("^[a-f0-9]{32}\\.(jpg|png|webp|pdf)$")]
    private static partial Regex KeyPattern();

    private string PathOf(string key)
    {
        if (!KeyPattern().IsMatch(key)) throw new ArgumentException("Geçersiz depolama anahtarı.", nameof(key));
        // İlk iki karakterle alt klasör: tek klasörde on binlerce dosya birikmesin.
        return Path.Combine(RootPath, key[..2], key);
    }

    public async Task SaveAsync(string storageKey, Stream content, CancellationToken ct = default)
    {
        var path = PathOf(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(fs, ct);
    }

    public Stream? OpenRead(string storageKey)
    {
        var path = PathOf(storageKey);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public void Delete(string storageKey)
    {
        var path = PathOf(storageKey);
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>Dosya türü, boyut ve içerik imzası (magic bytes) kontrolü. Yalnız uzantıya veya tarayıcının söylediği türe güvenilmez.</summary>
public static class FileValidator
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const string AcceptAttribute = "image/jpeg,image/png,image/webp,application/pdf";

    private static readonly Dictionary<string, (string Ext, string ContentType)> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = ("jpg", "image/jpeg"),
        [".jpeg"] = ("jpg", "image/jpeg"),
        [".png"] = ("png", "image/png"),
        [".webp"] = ("webp", "image/webp"),
        [".pdf"] = ("pdf", "application/pdf")
    };

    public record Result(bool Ok, string? Extension, string? ContentType, string? Error);

    public static Result Validate(string fileName, long length, ReadOnlySpan<byte> header)
    {
        if (length <= 0) return new(false, null, null, "Dosya boş.");
        if (length > MaxBytes) return new(false, null, null, $"Dosya en fazla {MaxBytes / 1024 / 1024} MB olabilir.");
        var ext = Path.GetExtension(fileName ?? "");
        if (!ByExtension.TryGetValue(ext, out var info))
            return new(false, null, null, "Yalnız JPG, PNG, WEBP fotoğraf ve PDF belge yüklenebilir.");
        if (!SignatureMatches(info.Ext, header))
            return new(false, null, null, "Dosyanın içeriği uzantısıyla uyuşmuyor; yüklenemedi.");
        return new(true, info.Ext, info.ContentType, null);
    }

    public static bool SignatureMatches(string ext, ReadOnlySpan<byte> h) => ext switch
    {
        "jpg" => h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF,
        "png" => h.Length >= 8 && h[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "webp" => h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("WEBP"u8),
        "pdf" => h.Length >= 5 && h[..5].SequenceEqual("%PDF-"u8),
        _ => false
    };

    /// <summary>Ekranda gösterilecek güvenli dosya adı (yol parçaları ve denetim karakterleri temizlenir).</summary>
    public static string SafeDisplayName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "dosya");
        name = new string(name.Where(c => !char.IsControl(c) && c is not ('<' or '>' or '"' or '\\' or '/')).ToArray()).Trim();
        if (name.Length == 0) name = "dosya";
        return name.Length > 200 ? name[^200..] : name;
    }
}
