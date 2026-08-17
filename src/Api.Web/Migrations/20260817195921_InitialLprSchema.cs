using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Api.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialLprSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Camaras",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Codigo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Ubicacion = table.Column<Point>(type: "geography", nullable: false),
                    TipoInstalacion = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    VelocidadMaximaKmh = table.Column<int>(type: "int", nullable: true),
                    Activa = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Camaras", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehiculosRobados",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PlateText = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    NumeroReporte = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FechaReporteUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RecuperadoAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehiculosRobados", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LecturasHistoricas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlateText = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CamaraId = table.Column<int>(type: "int", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    ImageReference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EsCoincidenciaBlacklist = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LecturasHistoricas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LecturasHistoricas_Camaras_CamaraId",
                        column: x => x.CamaraId,
                        principalTable: "Camaras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Alertas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LecturaHistoricaId = table.Column<long>(type: "bigint", nullable: false),
                    VehiculoRobadoId = table.Column<int>(type: "int", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AtendidaPor = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AtendidaAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alertas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alertas_LecturasHistoricas_LecturaHistoricaId",
                        column: x => x.LecturaHistoricaId,
                        principalTable: "LecturasHistoricas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Alertas_VehiculosRobados_VehiculoRobadoId",
                        column: x => x.VehiculoRobadoId,
                        principalTable: "VehiculosRobados",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alertas_LecturaHistoricaId",
                table: "Alertas",
                column: "LecturaHistoricaId");

            migrationBuilder.CreateIndex(
                name: "IX_Alertas_VehiculoRobadoId",
                table: "Alertas",
                column: "VehiculoRobadoId");

            migrationBuilder.CreateIndex(
                name: "IX_Camaras_Codigo",
                table: "Camaras",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LecturasHistoricas_CamaraId",
                table: "LecturasHistoricas",
                column: "CamaraId");

            migrationBuilder.CreateIndex(
                name: "IX_LecturasHistoricas_EventId",
                table: "LecturasHistoricas",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LecturasHistoricas_TimestampUtc",
                table: "LecturasHistoricas",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_VehiculosRobados_PlateText",
                table: "VehiculosRobados",
                column: "PlateText");

            // Nonclustered columnstore index: accelerates forensic/analytical scans over
            // LecturasHistoricas (millions of rows) without disturbing the rowstore PK used
            // by the high-throughput Dapper inserts on the hot path.
            migrationBuilder.Sql(
                """
                CREATE NONCLUSTERED COLUMNSTORE INDEX IX_LecturasHistoricas_Columnstore
                ON LecturasHistoricas (PlateText, CamaraId, TimestampUtc, Confidence, EsCoincidenciaBlacklist);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IX_LecturasHistoricas_Columnstore ON LecturasHistoricas;");

            migrationBuilder.DropTable(
                name: "Alertas");

            migrationBuilder.DropTable(
                name: "LecturasHistoricas");

            migrationBuilder.DropTable(
                name: "VehiculosRobados");

            migrationBuilder.DropTable(
                name: "Camaras");
        }
    }
}
