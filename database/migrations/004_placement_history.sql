-- En planta una PC cambia de estacion y de nombre. machineId es inmutable;
-- machine_code, site, area, line y station son editables por un administrador.
--
-- Una sola tabla en vez de machine_code_history + machine_location_history:
-- mover la PC y renombrarla es UN MISMO EVENTO, y partirlo en dos obligaria a
-- correlacionarlas por timestamp para reconstruir algo que nunca ocurrio por
-- separado.
--
-- Intervalo cerrado, mismo patron que machine_ip_history: valid_to NULL = vigente.

CREATE TABLE machine_placement_history (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  machine_id   CHAR(36)     NOT NULL,
  site_id      INT UNSIGNED NOT NULL,
  machine_code VARCHAR(64)  NOT NULL,
  area         VARCHAR(64)  NULL,
  line         VARCHAR(64)  NULL,
  station      VARCHAR(64)  NULL,
  valid_from   DATETIME(3)  NOT NULL,
  valid_to     DATETIME(3)  NULL,
  changed_by   VARCHAR(64)  NOT NULL,
  PRIMARY KEY (id),
  KEY ix_placement_machine (machine_id, valid_to),
  CONSTRAINT fk_placement_machine FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE CASCADE,
  CONSTRAINT fk_placement_site FOREIGN KEY (site_id) REFERENCES sites (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
