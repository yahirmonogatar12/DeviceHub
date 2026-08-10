-- Fase 3: entidad principal.
--   * id es un GUID generado por el agente. Inmutable. Nunca depende de la IP.
--   * NO hay columna `status`: se deriva de last_seen en cada consulta. Eso
--     elimina el job de fondo que estaria escribiendo cada segundo.
--   * Todas las fechas son DATETIME(3) en UTC (TIMESTAMP aplicaria conversion
--     implicita de zona horaria).
--   * Unicidad de machine_code POR SITIO, no global.

CREATE TABLE machines (
  id                     CHAR(36)     NOT NULL,
  site_id                INT UNSIGNED NOT NULL,
  machine_code           VARCHAR(64)  NOT NULL,
  display_name           VARCHAR(128) NULL,
  hostname               VARCHAR(128) NULL,
  area                   VARCHAR(64)  NULL,
  line                   VARCHAR(64)  NULL,
  station                VARCHAR(64)  NULL,
  current_ip             VARCHAR(45)  NULL,
  primary_mac            VARCHAR(17)  NULL,
  logged_user            VARCHAR(128) NULL,
  uptime_seconds         BIGINT       NULL,
  agent_version          VARCHAR(32)  NULL,
  token_hash             CHAR(64)     NULL,
  hardware_fingerprint   CHAR(64)     NULL,
  fingerprint_confidence ENUM('low','medium','high') NOT NULL DEFAULT 'low',
  identity_state         ENUM('ok','identity_conflict') NOT NULL DEFAULT 'ok',
  conflict_detected_at   DATETIME(3)  NULL,
  last_seen              DATETIME(3)  NULL,
  created_at             DATETIME(3)  NOT NULL,
  updated_at             DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_machines_site_code (site_id, machine_code),
  KEY ix_machines_last_seen (last_seen),
  -- Indice necesario para la degradacion aprendida: mismo fingerprint en >=3
  -- maquinas distintas => el valor no discrimina, se trata como LOW.
  KEY ix_machines_fingerprint (hardware_fingerprint),
  CONSTRAINT fk_machines_site FOREIGN KEY (site_id) REFERENCES sites (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
