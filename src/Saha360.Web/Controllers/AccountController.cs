using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Services;

namespace Saha360.Web.Controllers;

public class LoginInput
{
    [Required(ErrorMessage = "Kullanıcı adı veya e-posta zorunludur.")]
    [Display(Name = "Kullanıcı adı veya e-posta")]
    public string Login { get; set; } = "";

    [Required(ErrorMessage = "Parola zorunludur.")]
    [DataType(DataType.Password)]
    [Display(Name = "Parola")]
    public string Password { get; set; } = "";

    [Display(Name = "Beni hatırla")]
    public bool RememberMe { get; set; }
}

public class ChangePasswordInput
{
    [Required(ErrorMessage = "Mevcut parola zorunludur.")]
    public string CurrentPassword { get; set; } = "";

    [Required(ErrorMessage = "Yeni parola zorunludur.")]
    [MinLength(8, ErrorMessage = "Yeni parola en az 8 karakter olmalı.")]
    public string NewPassword { get; set; } = "";

    [Compare(nameof(NewPassword), ErrorMessage = "Parolalar eşleşmiyor.")]
    public string ConfirmPassword { get; set; } = "";
}

public class AccountController(SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users, AuditService audit,
    Saha360.Infrastructure.Persistence.AppDbContext db) : Controller
{
    [AllowAnonymous, HttpGet("/giris")]
    public IActionResult Login(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect("/");
        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginInput());
    }

    [AllowAnonymous, HttpPost("/giris"), EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginInput input, string? returnUrl)
    {
        ViewData["ReturnUrl"] = returnUrl;
        if (!ModelState.IsValid) return View(input);

        var login = input.Login.Trim();
        var user = await users.FindByNameAsync(login) ?? (login.Contains('@') ? await users.FindByEmailAsync(login) : null);
        // Hata mesajı genel: kullanıcının var olup olmadığı, pasif mi kilitli mi olduğu açığa çıkmaz.
        const string generic = "Kullanıcı adı veya parola hatalı.";
        if (user is null)
        {
            ModelState.AddModelError("", generic);
            return View(input);
        }
        var result = await signIn.PasswordSignInAsync(user, input.Password, input.RememberMe, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.IsLockedOut ? "Çok sayıda hatalı deneme. Birkaç dakika sonra tekrar deneyin." : generic);
            return View(input);
        }
        user.LastLoginAtUtc = DateTime.UtcNow;
        await users.UpdateAsync(user);
        audit.Add(new UserContext(user.Id, user.FullName, new HashSet<string>(), null, null, HttpContext.Connection.RemoteIpAddress?.ToString()),
            "Login", "User", user.Id, $"{user.UserName} giriş yaptı.");
        await db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        // Rolüne uygun ilk ekran: yalnız personel/şef olan kullanıcı doğrudan "İşlerim"e gider.
        var roles = await users.GetRolesAsync(user);
        var onlyField = roles.All(r => r is SystemRoles.Personnel);
        return Redirect(onlyField ? "/islerim" : "/");
    }

    [HttpPost("/cikis")]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return Redirect("/giris");
    }

    [AllowAnonymous, HttpGet("/yetkisiz")]
    public IActionResult Denied()
    {
        Response.StatusCode = 403;
        ViewData["Code"] = 403;
        ViewData["Message"] = "Bu sayfayı görme yetkiniz yok.";
        return View("~/Views/Shared/Status.cshtml");
    }

    [HttpGet("/parola")]
    public IActionResult ChangePassword() => View(new ChangePasswordInput());

    [HttpPost("/parola")]
    public async Task<IActionResult> ChangePassword(ChangePasswordInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var user = await users.GetUserAsync(User);
        if (user is null) return Redirect("/giris");
        var r = await users.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword);
        if (!r.Succeeded)
        {
            foreach (var e in r.Errors) ModelState.AddModelError("", e.Description);
            return View(input);
        }
        await signIn.RefreshSignInAsync(user);
        audit.Add(new UserContext(user.Id, user.FullName, new HashSet<string>(), null, null), "PasswordChanged", "User", user.Id,
            $"{user.UserName} parolasını değiştirdi.");
        await db.SaveChangesAsync();
        TempData["Ok"] = "Parolanız değiştirildi.";
        return Redirect("/");
    }
}
