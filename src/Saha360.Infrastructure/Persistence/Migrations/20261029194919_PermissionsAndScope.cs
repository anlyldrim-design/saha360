using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saha360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PermissionsAndScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllUnits",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Granted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleName_Permission",
                table: "RolePermissions",
                columns: new[] { "RoleName", "Permission" },
                unique: true);

            // Eski davranışı koru: birimi boş bırakılmış yönetici / planlamacı / rapor hesapları önceden tüm birimleri görüyordu.
            migrationBuilder.Sql("""
                UPDATE u SET AllUnits = 1
                FROM AspNetUsers u
                WHERE u.OrganizationUnitId IS NULL AND EXISTS (
                    SELECT 1 FROM AspNetUserRoles ur JOIN AspNetRoles r ON r.Id = ur.RoleId
                    WHERE ur.UserId = u.Id AND r.Name IN ('BirimYoneticisi', 'Planlamaci', 'RaporGoruntuleyici'))
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropColumn(
                name: "AllUnits",
                table: "AspNetUsers");
        }
    }
}
