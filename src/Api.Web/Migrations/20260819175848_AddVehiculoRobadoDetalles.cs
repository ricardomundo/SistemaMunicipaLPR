using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Api.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddVehiculoRobadoDetalles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Anio",
                table: "VehiculosRobados",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Clase",
                table: "VehiculosRobados",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "VehiculosRobados",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImagenPath",
                table: "VehiculosRobados",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Marca",
                table: "VehiculosRobados",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarcasUOtros",
                table: "VehiculosRobados",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Modelo",
                table: "VehiculosRobados",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Anio",
                table: "VehiculosRobados");

            migrationBuilder.DropColumn(
                name: "Clase",
                table: "VehiculosRobados");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "VehiculosRobados");

            migrationBuilder.DropColumn(
                name: "ImagenPath",
                table: "VehiculosRobados");

            migrationBuilder.DropColumn(
                name: "Marca",
                table: "VehiculosRobados");

            migrationBuilder.DropColumn(
                name: "MarcasUOtros",
                table: "VehiculosRobados");

            migrationBuilder.DropColumn(
                name: "Modelo",
                table: "VehiculosRobados");
        }
    }
}
