using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetadataApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Company = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Website = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    License = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Maintainers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Maintainers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationMaintainers",
                columns: table => new
                {
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MaintainerId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationMaintainers", x => new { x.ApplicationId, x.MaintainerId });
                    table.ForeignKey(
                        name: "FK_ApplicationMaintainers_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationMaintainers_Maintainers_MaintainerId",
                        column: x => x.MaintainerId,
                        principalTable: "Maintainers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationMaintainers_MaintainerId",
                table: "ApplicationMaintainers",
                column: "MaintainerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationMaintainers");

            migrationBuilder.DropTable(
                name: "Applications");

            migrationBuilder.DropTable(
                name: "Maintainers");
        }
    }
}
