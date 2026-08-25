-- =============================================================================
-- Esquema de RedLists (gestión de listas de vehículos), consolidado dentro de
-- la base de datos compartida `SistemaLPR` de este proyecto -- ver
-- docs/fases.md, sección "Fase 3.5 -- Integración con RedLists".
--
-- Origen: adaptado de RedLists (repo separado, C:\Ric68\RedLists,
-- db/mysql/init/00_create_database_and_users.sql + 01_vehicle_lists_schema.sql
-- + 02_external_vehicle_feed_schema.sql + 03_seed_data.sql + 99_grants.sql),
-- con dos cambios respecto al original:
--   1. No crea una base de datos nueva -- usa la `SistemaLPR` que ya crea el
--      docker-compose.yml de este repo (variable MYSQL_DATABASE), en vez de
--      la base `redlists` separada que usaba RedLists por su cuenta.
--   2. Los GRANT de los usuarios de aplicación (vehiclelists_svc,
--      externalfeed_svc) se acotan a `SistemaLPR.*` en vez de `redlists.*`.
--
-- El docker-compose.yml propio de RedLists (RedLists/db/docker-compose.yml) y
-- su base de datos `redlists` separada quedan obsoletos a partir de aquí --
-- este script es la fuente de verdad del esquema de RedLists en este proyecto.
--
-- Cómo aplicarlo (tools/setup-new-machine.ps1 ya lo hace automáticamente):
--   docker compose exec -T mysql mysql -uroot -p"Lpr#Dev_2026!" SistemaLPR < db/redlists-schema.sql
--
-- Es seguro volver a correrlo -- todo usa IF NOT EXISTS / ON DUPLICATE KEY /
-- WHERE NOT EXISTS, igual que en el script original de RedLists.
-- =============================================================================

USE SistemaLPR;

-- -----------------------------------------------------------------------------
-- Usuarios de aplicación con permisos acotados a las tablas de su propio
-- servicio. Es una única base de datos física (compartida con el resto de
-- SistemaMunicipaLPR), pero cada microservicio de RedLists sigue teniendo su
-- propio límite de acceso -- defensa en profundidad para que un bug en un
-- servicio no pueda escribir en las tablas del otro, ni en las de
-- SistemaMunicipaLPR (Cámaras, LecturasHistoricas, Alertas, etc).
--
-- IMPORTANTE: las contraseñas de abajo son SOLO para desarrollo local. En
-- cualquier ambiente real, cámbialas (ALTER USER después de correr esto) y
-- nunca las dejes en un script versionado con un valor real.
-- -----------------------------------------------------------------------------
CREATE USER IF NOT EXISTS 'vehiclelists_svc'@'%' IDENTIFIED BY 'change_me_vehiclelists_dev_only';
CREATE USER IF NOT EXISTS 'externalfeed_svc'@'%' IDENTIFIED BY 'change_me_externalfeed_dev_only';

-- -----------------------------------------------------------------------------
-- Esquema de VehicleListsService (agregado Vehicle + ObjectsList).
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS vehicle_lists (
    id                          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    global_id                   CHAR(36)        NOT NULL,
    name                        VARCHAR(200)    NOT NULL,
    description                 VARCHAR(500)    NULL,
    list_type                   ENUM('Vehicles','WhiteList') NOT NULL DEFAULT 'Vehicles',
    is_external_source          TINYINT(1)      NOT NULL DEFAULT 0,
    is_stolen_external_source   TINYINT(1)      NOT NULL DEFAULT 0,
    external_adapter_type       VARCHAR(100)    NULL,
    creating_user               INT             NOT NULL DEFAULT 0,
    last_modifying_user         INT             NOT NULL DEFAULT 0,
    creation_time               DATETIME(6)     NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    last_modified               DATETIME(6)     NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
                                                 ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    UNIQUE KEY uq_vehicle_lists_global_id (global_id),
    KEY idx_vehicle_lists_type (list_type),
    KEY idx_vehicle_lists_external (is_stolen_external_source, external_adapter_type)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS vehicles (
    id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    plate_number        VARCHAR(20)     NOT NULL,
    plate_template      VARCHAR(50)     NULL,
    brand               VARCHAR(100)    NULL,
    model               VARCHAR(100)    NULL,
    color               VARCHAR(50)     NULL,
    vehicle_class       VARCHAR(50)     NULL,
    year_manufactured   SMALLINT        NULL,
    vin                 VARCHAR(50)     NULL,
    additional_info     VARCHAR(500)    NULL,
    created_at          DATETIME(6)     NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at          DATETIME(6)     NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
                                        ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    KEY idx_vehicles_plate (plate_number)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Membresía de un vehículo en una lista + estado de recuperación.
CREATE TABLE IF NOT EXISTS list_vehicles (
    list_id             BIGINT UNSIGNED NOT NULL,
    vehicle_id          BIGINT UNSIGNED NOT NULL,
    added_at            DATETIME(6)     NOT NULL DEFAULT CURRENT_TIMESTAMP(6),

    -- Bandera "soft" de recuperado. recovered_by_org_id = 0 significa "no
    -- recuperado" -- BlacklistCacheService (Service.Inference) filtra por
    -- esto al armar la caché de Redis. Ver Fase 3.5 en docs/fases.md.
    recovered_by_org_id INT             NOT NULL DEFAULT 0,
    recovered_date      DATETIME(6)     NULL,
    recovered_by_text   VARCHAR(200)    NULL,

    PRIMARY KEY (list_id, vehicle_id),
    KEY idx_list_vehicles_vehicle (vehicle_id),
    CONSTRAINT fk_list_vehicles_list
        FOREIGN KEY (list_id) REFERENCES vehicle_lists(id) ON DELETE CASCADE,
    CONSTRAINT fk_list_vehicles_vehicle
        FOREIGN KEY (vehicle_id) REFERENCES vehicles(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- -----------------------------------------------------------------------------
-- Esquema de ExternalVehicleFeedService (adaptadores + auditoría de sync).
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS adapters (
    id                          VARCHAR(50)     NOT NULL,
    display_name                VARCHAR(200)    NOT NULL,
    is_active                   TINYINT(1)      NOT NULL DEFAULT 0,
    polling_interval_minutes    INT             NOT NULL DEFAULT 60,
    created_at                  DATETIME(6)     NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    updated_at                  DATETIME(6)     NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
                                                 ON UPDATE CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS sync_runs (
    id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    adapter_id          VARCHAR(50)     NOT NULL,
    started_at          DATETIME(6)     NOT NULL,
    finished_at         DATETIME(6)     NULL,
    success             TINYINT(1)      NULL,
    vehicles_imported   INT             NOT NULL DEFAULT 0,
    error_message       VARCHAR(2000)   NULL,
    PRIMARY KEY (id),
    KEY idx_sync_runs_adapter (adapter_id, started_at),
    CONSTRAINT fk_sync_runs_adapter FOREIGN KEY (adapter_id) REFERENCES adapters(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS imported_vehicles_log (
    id                              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    sync_run_id                     BIGINT UNSIGNED NOT NULL,
    external_id                     VARCHAR(100)    NULL,
    plate                           VARCHAR(20)     NOT NULL,
    raw_state                       VARCHAR(10)     NULL,
    theft_date                      DATETIME(6)     NULL,
    imported_at                     DATETIME(6)     NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    forwarded_to_vehicle_lists      TINYINT(1)      NOT NULL DEFAULT 0,
    forward_error                   VARCHAR(1000)   NULL,
    PRIMARY KEY (id),
    KEY idx_imported_vehicles_run (sync_run_id),
    KEY idx_imported_vehicles_plate (plate),
    CONSTRAINT fk_imported_vehicles_run FOREIGN KEY (sync_run_id) REFERENCES sync_runs(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;



ALTER DATABASE CHARACTER SET utf8mb4;


CREATE TABLE `casbin_rule` (
    `id` int NOT NULL AUTO_INCREMENT,
    `ptype` varchar(255) CHARACTER SET utf8mb4 NULL,
    `v0` varchar(255) CHARACTER SET utf8mb4 NULL,
    `v1` varchar(255) CHARACTER SET utf8mb4 NULL,
    `v2` varchar(255) CHARACTER SET utf8mb4 NULL,
    `v3` varchar(255) CHARACTER SET utf8mb4 NULL,
    `v4` varchar(255) CHARACTER SET utf8mb4 NULL,
    `v5` varchar(255) CHARACTER SET utf8mb4 NULL,
    `v6` longtext CHARACTER SET utf8mb4 NULL,
    `v7` longtext CHARACTER SET utf8mb4 NULL,
    `v8` longtext CHARACTER SET utf8mb4 NULL,
    `v9` longtext CHARACTER SET utf8mb4 NULL,
    `v10` longtext CHARACTER SET utf8mb4 NULL,
    `v11` longtext CHARACTER SET utf8mb4 NULL,
    `v12` longtext CHARACTER SET utf8mb4 NULL,
    `v13` longtext CHARACTER SET utf8mb4 NULL,
    CONSTRAINT `PK_casbin_rule` PRIMARY KEY (`id`)
) CHARACTER SET=utf8mb4;


CREATE INDEX `IX_casbin_rule_ptype` ON `casbin_rule` (`ptype`);


CREATE INDEX `IX_casbin_rule_v0` ON `casbin_rule` (`v0`);


CREATE INDEX `IX_casbin_rule_v1` ON `casbin_rule` (`v1`);


CREATE INDEX `IX_casbin_rule_v2` ON `casbin_rule` (`v2`);


CREATE INDEX `IX_casbin_rule_v3` ON `casbin_rule` (`v3`);


CREATE INDEX `IX_casbin_rule_v4` ON `casbin_rule` (`v4`);


CREATE INDEX `IX_casbin_rule_v5` ON `casbin_rule` (`v5`);

-- -----------------------------------------------------------------------------
-- Datos semilla mínimos de desarrollo (idéntico al seed original de RedLists).
-- -----------------------------------------------------------------------------
INSERT INTO adapters (id, display_name, is_active, polling_interval_minutes)
VALUES ('C5M', 'C5M (simulado)', 1, 60)
ON DUPLICATE KEY UPDATE display_name = VALUES(display_name);

INSERT INTO vehicle_lists (global_id, name, description, list_type, is_stolen_external_source, external_adapter_type)
SELECT UUID(), 'RedList Demo', 'Lista de ejemplo creada por el seed de desarrollo', 'Vehicles', 1, 'C5M'
WHERE NOT EXISTS (SELECT 1 FROM vehicle_lists WHERE name = 'RedList Demo');

-- -----------------------------------------------------------------------------
-- Permisos.
-- -----------------------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE, DELETE ON SistemaLPR.vehicle_lists TO 'vehiclelists_svc'@'%';
GRANT SELECT, INSERT, UPDATE, DELETE ON SistemaLPR.vehicles TO 'vehiclelists_svc'@'%';
GRANT SELECT, INSERT, UPDATE, DELETE ON SistemaLPR.list_vehicles TO 'vehiclelists_svc'@'%';

GRANT SELECT, INSERT, UPDATE, DELETE ON SistemaLPR.adapters TO 'externalfeed_svc'@'%';
GRANT SELECT, INSERT, UPDATE, DELETE ON SistemaLPR.sync_runs TO 'externalfeed_svc'@'%';
GRANT SELECT, INSERT, UPDATE, DELETE ON SistemaLPR.imported_vehicles_log TO 'externalfeed_svc'@'%';

-- Service.Inference necesita leer las tablas de VehicleListsService para su
-- BlacklistCacheService (ver Fase 3.5 en docs/fases.md). En desarrollo local
-- usa la cuenta root de docker-compose (misma que usa para LecturasHistoricas
-- etc.), así que no hace falta un usuario de solo lectura aparte todavía --
-- si eso cambia, dar de alta un usuario de lectura acotado aquí.

FLUSH PRIVILEGES;
