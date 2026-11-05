using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saha360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TitleRestrictedTeamGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOpenToAll",
                table: "JobRoles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WorkTeamGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkOrderId = table.Column<int>(type: "int", nullable: false),
                    JobRoleId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTeamGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkTeamGroups_JobRoles_JobRoleId",
                        column: x => x.JobRoleId,
                        principalTable: "JobRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkTeamGroups_WorkOrders_WorkOrderId",
                        column: x => x.WorkOrderId,
                        principalTable: "WorkOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTeamGroups_JobRoleId",
                table: "WorkTeamGroups",
                column: "JobRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTeamGroups_WorkOrderId_JobRoleId",
                table: "WorkTeamGroups",
                columns: new[] { "WorkOrderId", "JobRoleId" },
                unique: true);

            // Şoför görevi kısıtlamasız: her personel atanabilir (kurum kararı; Tanımlar ekranından değiştirilebilir).
            migrationBuilder.Sql("UPDATE [JobRoles] SET [IsOpenToAll] = 1 WHERE [Name] = N'Şoför';");

            // Mevcut işlerin ekip alanları, şu an ekipte olan kişilerin alanlarından doldurulur.
            migrationBuilder.Sql(@"
INSERT INTO [WorkTeamGroups] ([WorkOrderId], [JobRoleId], [SortOrder])
SELECT x.[WorkOrderId], x.[JobRoleId], x.[SortOrder]
FROM (SELECT DISTINCT a.[WorkOrderId], a.[JobRoleId], r.[SortOrder]
      FROM [WorkAssignments] a JOIN [JobRoles] r ON r.[Id] = a.[JobRoleId]
      WHERE a.[ReleasedAtUtc] IS NULL) x;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkTeamGroups");

            migrationBuilder.DropColumn(
                name: "IsOpenToAll",
                table: "JobRoles");
        }
    }
}
