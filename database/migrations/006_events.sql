-- Eventos de maquina. En Fase 0-4 lo usa la deteccion de identidad; en Fase 12
-- convive con machine_audit (que registra acciones DE USUARIO, no del sistema).

CREATE TABLE machine_events (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  machine_id CHAR(36)    NULL,
  event_type VARCHAR(64) NOT NULL,
  details    TEXT        NULL,
  source_ip  VARCHAR(45) NULL,
  created_at DATETIME(3) NOT NULL,
  PRIMARY KEY (id),
  KEY ix_events_machine (machine_id, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
