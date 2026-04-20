using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace SlotAd_Globe.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await db.Database.MigrateAsync(cancellationToken);

        if (!await db.Users.AnyAsync(cancellationToken))
        {
            var userName = configuration["Auth:SeedAdmin:UserName"];
            var password = configuration["Auth:SeedAdmin:Password"];
            if (!string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(password))
            {
                var hasher = new PasswordHasher<AppUser>();
                var user = new AppUser
                {
                    Id = Guid.NewGuid(),
                    UserName = userName.Trim(),
                    CreatedUtc = DateTime.UtcNow,
                    IsAdmin = true
                };
                user.PasswordHash = hasher.HashPassword(user, password);
                db.Users.Add(user);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        await PromoteConfiguredAdminAsync(db, configuration, cancellationToken);
    }

    private static async Task PromoteConfiguredAdminAsync(
        AppDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var adminName = configuration["Auth:SeedAdmin:UserName"];
        if (string.IsNullOrWhiteSpace(adminName))
            return;

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.UserName == adminName.Trim(),
            cancellationToken);
        if (user is { IsAdmin: false })
        {
            user.IsAdmin = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
