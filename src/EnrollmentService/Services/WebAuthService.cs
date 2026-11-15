using System.Security.Cryptography;
using System.Text;
using EnrollmentService.Domain;
using EnrollmentService.Repositories;

namespace EnrollmentService.Services;

public sealed class WebAuthService
{
    private const int Pbkdf2Iterations = 100_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    private readonly AuthRepository _authRepo;
    private readonly EnrollmentRepository _enrollmentRepo;
    private readonly ServiceOptions _options;

    public WebAuthService(
        AuthRepository authRepo,
        EnrollmentRepository enrollmentRepo,
        ServiceOptions options)
    {
        _authRepo = authRepo;
        _enrollmentRepo = enrollmentRepo;
        _options = options;
    }

    public static (string HashBase64, string SaltBase64) HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyPassword(string password, string expectedHashBase64, string saltBase64)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(expectedHashBase64) || string.IsNullOrWhiteSpace(saltBase64))
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(saltBase64);
            byte[] expectedHash = Convert.FromBase64String(expectedHashBase64);
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string GenerateOpaqueToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        string base64Url = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"wbt_{base64Url}";
    }

    public static string HashToken(string rawToken)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task SyncConfiguredDemoPasswordsAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals(_options.DemoStudentPassword, "StudentPass2026!", StringComparison.Ordinal))
        {
            var (hash, salt) = HashPassword(_options.DemoStudentPassword);
            await _authRepo.UpdateRolePasswordsAsync("user", hash, salt, cancellationToken);
        }

        if (!string.Equals(_options.DemoStaffPassword, "StaffPass2026!", StringComparison.Ordinal))
        {
            var (hash, salt) = HashPassword(_options.DemoStaffPassword);
            await _authRepo.UpdateRolePasswordsAsync("staff", hash, salt, cancellationToken);
        }
    }

    public async Task<ServiceResult<WebLoginResponseDto>> LoginAsync(
        WebLoginRequestDto request,
        CancellationToken cancellationToken = default)
    {
        string? loginRaw = (request.Username ?? request.Login ?? request.UserId)?.Trim();
        string? password = request.Password;

        if (string.IsNullOrWhiteSpace(loginRaw) || string.IsNullOrEmpty(password))
        {
            return ServiceResult<WebLoginResponseDto>.Failure(
                StatusCodes.Status400BadRequest,
                "missing_credentials",
                "Відсутні облікові дані",
                "Поля username та password є обов'язковими для входу.");
        }

        var user = await _authRepo.GetUserByLoginAsync(loginRaw, cancellationToken);
        if (user == null || !VerifyPassword(password, user.PasswordHash, user.PasswordSalt))
        {
            StructuredJsonLogger.LogEvent(_options.InstanceId, "auth.login.failed", new Dictionary<string, object?>
            {
                ["username"] = loginRaw
            });

            return ServiceResult<WebLoginResponseDto>.Failure(
                StatusCodes.Status401Unauthorized,
                "invalid_credentials",
                "Невірні облікові дані",
                "Невірне ім'я користувача або пароль.");
        }

        int ttlMinutes = Math.Max(1, _options.AuthTokenTtlMinutes);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = nowUtc.AddMinutes(ttlMinutes);

        string rawToken = GenerateOpaqueToken();
        string tokenHash = HashToken(rawToken);

        await _authRepo.CreateTokenAsync(
            tokenHash,
            user.Id,
            user.Username,
            user.Role,
            user.StudentId,
            expiresAt,
            cancellationToken);

        StructuredJsonLogger.LogEvent(_options.InstanceId, "auth.login.succeeded", new Dictionary<string, object?>
        {
            ["user_id"] = user.Id,
            ["role"] = user.Role
        });

        var profile = new WebUserProfileDto(
            user.Id,
            user.Username,
            user.Role,
            user.StudentId,
            user.Email,
            user.FullName);

        return ServiceResult<WebLoginResponseDto>.Ok(new WebLoginResponseDto(
            rawToken,
            rawToken,
            "Bearer",
            ttlMinutes * 60,
            expiresAt,
            profile));
    }

    public async Task<AuthenticatedWebUser?> ValidateBearerTokenAsync(
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        // Суворе розмежування: службовий токен РПО (API_AUTH_TOKEN) не може виступати як Web-токен користувача.
        if (string.Equals(rawToken, _options.ApiAuthToken, StringComparison.Ordinal))
        {
            return null;
        }

        string tokenHash = HashToken(rawToken);
        return await _authRepo.ValidateTokenAsync(tokenHash, cancellationToken);
    }

    public async Task<bool> LogoutAsync(AuthenticatedWebUser user, CancellationToken cancellationToken = default)
    {
        bool revoked = await _authRepo.RevokeTokenAsync(user.TokenHash, cancellationToken);
        StructuredJsonLogger.LogEvent(_options.InstanceId, "auth.logout", new Dictionary<string, object?>
        {
            ["user_id"] = user.UserId,
            ["role"] = user.Role,
            ["revoked"] = revoked
        });
        return revoked;
    }

    public async Task<ServiceResult<WebMeResponseDto>> GetMeAsync(
        AuthenticatedWebUser caller,
        CancellationToken cancellationToken = default)
    {
        var user = await _authRepo.GetUserByIdAsync(caller.UserId, cancellationToken);
        if (user == null)
        {
            return ServiceResult<WebMeResponseDto>.Failure(
                StatusCodes.Status401Unauthorized,
                "unauthorized",
                "Неавторизований доступ",
                "Обліковий запис поточного токена не знайдено.");
        }

        int? maxDisciplines = null;
        int? activeCount = null;
        decimal? activeEcts = null;
        decimal? maxEcts = null;

        if (!string.IsNullOrWhiteSpace(user.StudentId))
        {
            var student = await _enrollmentRepo.GetStudentByIdAsync(user.StudentId, cancellationToken);
            if (student != null)
            {
                maxDisciplines = student.MaxDisciplines;
                activeCount = student.ActiveCount;
                activeEcts = student.ActiveEcts;
                maxEcts = EnrollmentDomainRules.MaxSemesterEcts;
            }
        }

        return ServiceResult<WebMeResponseDto>.Ok(new WebMeResponseDto(
            user.Id,
            user.Username,
            user.Role,
            user.StudentId,
            user.Email,
            user.FullName,
            maxDisciplines,
            activeCount,
            activeEcts,
            maxEcts,
            _options.InstanceId));
    }
}
