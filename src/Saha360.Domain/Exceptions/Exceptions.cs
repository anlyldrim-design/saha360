namespace Saha360.Domain.Exceptions;

/// <summary>İş kuralı ihlali. Mesajı kullanıcıya olduğu gibi gösterilir; bu yüzden Türkçe ve anlaşılır yazılmalı.</summary>
public class DomainException(string message) : Exception(message);

/// <summary>Kullanıcının bu işlem için yetkisi yok (403).</summary>
public class ForbiddenException(string message = "Bu işlem için yetkiniz yok.") : Exception(message);

/// <summary>Kayıt bulunamadı veya kullanıcının görme yetkisi yok (404). İkisi bilinçli olarak ayırt edilmez.</summary>
public class NotFoundException(string message = "Kayıt bulunamadı.") : Exception(message);
