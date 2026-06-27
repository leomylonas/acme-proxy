using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcmeProxy.Migrations
{
    /// <inheritdoc />
    public partial class AddLetsEncryptEnvironmentFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LetsEncryptEnvironment",
                table: "Orders",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Environment",
                table: "LeAccounts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LetsEncryptEnvironment",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Environment",
                table: "LeAccounts");
        }
    }
}
