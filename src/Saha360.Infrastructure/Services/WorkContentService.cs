using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.FileStorage;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

/// <summary>İşe not, malzeme ve fotoğraf/dosya ekleme. Her ekleme ve kaldırma işlem geçmişine yazılır.</summary>
public class WorkContentService(AppDbContext db, AccessService access, AuditService audit, IFileStorage storage)
{
    public const decimal MaxQuantity = 1_000_000m;

    public async Task<OpResult> AddNoteAsync(UserContext u, int workId, string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.Length == 0) throw new DomainException("Not boş olamaz.");
        if (t.Length > 4000) throw new DomainException("Not en fazla 4000 karakter olabilir.");
        var w = await LoadForContributionAsync(u, workId);
        db.WorkNotes.Add(new WorkNote { WorkOrderId = w.Id, AuthorUserId = u.UserId, NoteText = t });
        audit.Add(u, "NoteAdded", "WorkOrder", w.Id, "Not eklendi.", w.Id);
        await db.SaveChangesAsync();
        return OpResult.Done("Not eklendi.");
    }

    public async Task<OpResult> AddMaterialAsync(UserContext u, int workId, int materialId, decimal quantity, string? note)
    {
        var w = await LoadForContributionAsync(u, workId);
        await AddMaterialCoreAsync(u, w, materialId, quantity, note);
        await db.SaveChangesAsync();
        return OpResult.Done("Malzeme kullanımı kaydedildi.");
    }

    /// <summary>Aynı malzeme ve birim için yeni satır açılmaz; miktar birleştirilir. Stok düşülmez (stok tutulmuyor).</summary>
    internal async Task AddMaterialCoreAsync(UserContext u, WorkOrder w, int materialId, decimal quantity, string? note)
    {
        if (quantity <= 0) throw new DomainException("Malzeme miktarı sıfırdan büyük olmalı.");
        if (quantity > MaxQuantity) throw new DomainException("Malzeme miktarı çok büyük.");
        if (decimal.Round(quantity, 3) != quantity) throw new DomainException("Miktar en fazla 3 ondalık basamak içerebilir.");
        var m = await db.MaterialCatalog.FirstOrDefaultAsync(x => x.Id == materialId && x.IsActive)
                ?? throw new DomainException("Seçilen malzeme katalogda yok veya pasif.");

        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)];
        var row = db.WorkMaterials.Local.FirstOrDefault(x => x.WorkOrderId == w.Id && x.MaterialCatalogId == m.Id && x.UnitSnapshot == m.Unit)
                  ?? (w.Id == 0 ? null : await db.WorkMaterials.FirstOrDefaultAsync(x => x.WorkOrderId == w.Id && x.MaterialCatalogId == m.Id && x.UnitSnapshot == m.Unit));
        if (row is null)
        {
            row = new WorkMaterial
            {
                WorkOrderId = w.Id, MaterialCatalogId = m.Id, MaterialNameSnapshot = m.Name, UnitSnapshot = m.Unit, Quantity = quantity,
                AddedByUserId = u.UserId, Note = cleanNote
            };
            db.WorkMaterials.Add(row);
        }
        else
        {
            row.Quantity += quantity;
            row.UpdatedAtUtc = DateTime.UtcNow;
            if (cleanNote is not null) row.Note = row.Note is null ? cleanNote : $"{row.Note} / {cleanNote}"[..Math.Min(500, row.Note.Length + 3 + cleanNote.Length)];
        }
        audit.Add(u, "MaterialAdded", "WorkOrder", w.Id, $"Malzeme: {m.Name} {quantity:0.###} {m.Unit}", w.Id,
            new { m.Code, quantity });
    }

    public async Task<OpResult> RemoveMaterialAsync(UserContext u, int workId, int rowId)
    {
        var w = await access.LoadVisibleAsync(u, workId);
        if (!WorkflowRules.AcceptsContent(w.Status)) throw new DomainException("Kapanmış işte malzeme değiştirilemez.");
        if (!access.CanManage(u, w) && !access.CanApprove(u, w)) throw new ForbiddenException("Malzeme satırını yalnız planlamacı veya şef kaldırabilir.");
        var row = await db.WorkMaterials.FirstOrDefaultAsync(x => x.Id == rowId && x.WorkOrderId == w.Id) ?? throw new NotFoundException();
        db.WorkMaterials.Remove(row);
        audit.Add(u, "MaterialRemoved", "WorkOrder", w.Id, $"Malzeme satırı kaldırıldı: {row.MaterialNameSnapshot} {row.Quantity:0.###} {row.UnitSnapshot}", w.Id);
        await db.SaveChangesAsync();
        return OpResult.Done("Malzeme satırı kaldırıldı.");
    }

    public async Task<OpResult> AddAttachmentAsync(UserContext u, int workId, UploadInput input)
    {
        var w = await LoadForContributionAsync(u, workId);
        var key = await AddAttachmentCoreAsync(u, w, input);
        try { await db.SaveChangesAsync(); }
        catch { storage.Delete(key); throw; }
        return OpResult.Done("Dosya yüklendi.");
    }

    /// <summary>Dosyayı doğrular ve depoya yazar; kaydı bağlama ekler. Dönen anahtar, hata olursa dosyayı silmek içindir.</summary>
    internal async Task<string> AddAttachmentCoreAsync(UserContext u, WorkOrder w, UploadInput input)
    {
        if (!Enum.IsDefined(input.Type)) throw new DomainException("Geçersiz dosya türü.");
        var header = new byte[16];
        var read = await input.Content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
        var check = FileValidator.Validate(input.FileName, input.Length, header.AsSpan(0, read));
        if (!check.Ok) throw new DomainException($"{FileValidator.SafeDisplayName(input.FileName)}: {check.Error}");
        if (input.Content.CanSeek) input.Content.Position = 0;
        else throw new DomainException("Dosya okunamadı.");

        var key = $"{Guid.NewGuid():N}.{check.Extension}";
        await storage.SaveAsync(key, input.Content);
        var name = FileValidator.SafeDisplayName(input.FileName);
        var caption = string.IsNullOrWhiteSpace(input.Caption) ? null : input.Caption.Trim()[..Math.Min(input.Caption.Trim().Length, 500)];
        db.WorkAttachments.Add(new WorkAttachment
        {
            WorkOrderId = w.Id, UploadedByUserId = u.UserId, StorageKey = key, OriginalFileName = name, ContentType = check.ContentType!,
            FileSizeBytes = input.Length, Caption = caption, AttachmentType = input.Type
        });
        audit.Add(u, "AttachmentAdded", "WorkOrder", w.Id, $"{Domain.Enums.TrNames.Of(input.Type)} yüklendi: {name}", w.Id);
        return key;
    }

    /// <summary>Dosya listeden kaldırılır (kaydı ve dosyası saklanır; geçmişte görünür).</summary>
    public async Task<OpResult> DeleteAttachmentAsync(UserContext u, int workId, int attachmentId)
    {
        var w = await access.LoadVisibleAsync(u, workId);
        var att = await db.WorkAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId && a.WorkOrderId == w.Id && a.DeletedAtUtc == null)
                  ?? throw new NotFoundException("Dosya bulunamadı.");
        var own = att.UploadedByUserId == u.UserId && WorkflowRules.AcceptsContent(w.Status);
        if (!own && !access.CanManage(u, w)) throw new ForbiddenException("Bu dosyayı kaldırma yetkiniz yok.");
        att.DeletedAtUtc = DateTime.UtcNow;
        att.DeletedByUserId = u.UserId;
        audit.Add(u, "AttachmentRemoved", "WorkOrder", w.Id, $"Dosya kaldırıldı: {att.OriginalFileName}", w.Id);
        await db.SaveChangesAsync();
        return OpResult.Done("Dosya kaldırıldı.");
    }

    /// <summary>Dosyayı yalnız işi görmeye yetkili kullanıcıya açar. Adres bilinse bile yetkisiz kişi erişemez.</summary>
    public async Task<(Stream Stream, string ContentType, string FileName)> OpenAttachmentAsync(UserContext u, int attachmentId)
    {
        var att = await db.WorkAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId && a.DeletedAtUtc == null)
                  ?? throw new NotFoundException("Dosya bulunamadı.");
        if (!await access.VisibleWorks(u).AnyAsync(w => w.Id == att.WorkOrderId)) throw new NotFoundException("Dosya bulunamadı.");
        var stream = storage.OpenRead(att.StorageKey) ?? throw new NotFoundException("Dosya depoda bulunamadı.");
        return (stream, att.ContentType, att.OriginalFileName);
    }

    internal void DeleteFiles(IEnumerable<string> keys)
    {
        foreach (var k in keys)
        {
            try { storage.Delete(k); } catch (IOException) { }
        }
    }

    private async Task<WorkOrder> LoadForContributionAsync(UserContext u, int workId)
    {
        var w = await access.LoadVisibleAsync(u, workId);
        if (!WorkflowRules.AcceptsContent(w.Status)) throw new DomainException("Tamamlanmış veya iptal edilmiş işe kayıt eklenemez.");
        if (!access.CanContribute(u, w)) throw new ForbiddenException("Bu işe kayıt ekleme yetkiniz yok.");
        return w;
    }
}
