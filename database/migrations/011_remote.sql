-- Fases 10 y 11: control remoto y sesiones.
--
-- Las columnas son deliberadamente OPACAS: `remote_provider` y `remote_device_id`
-- son texto sin estructura. La base no sabe que existe RustDesk, igual que no lo
-- sabe el contrato ni el dashboard. Cambiar de motor en la Fase 18 no migra nada.

ALTER TABLE machines
  ADD COLUMN remote_provider  VARCHAR(32)  NULL AFTER agent_version,
  ADD COLUMN remote_device_id VARCHAR(64)  NULL AFTER remote_provider,
  ADD COLUMN remote_available TINYINT(1)   NOT NULL DEFAULT 0 AFTER remote_device_id;

-- Toda sesion de control remoto queda registrada: quien, sobre que maquina,
-- desde donde y cuando. Es la base de la auditoria de la Fase 12 para la accion
-- mas sensible del sistema.
CREATE TABLE machine_sessions (
  id          CHAR(36)    NOT NULL,
  machine_id  CHAR(36)    NOT NULL,
  user_id     VARCHAR(64) NOT NULL,
  provider    VARCHAR(32) NOT NULL,
  device_id   VARCHAR(64) NULL,
  source_ip   VARCHAR(45) NULL,
  started_at  DATETIME(3) NOT NULL,
  ended_at    DATETIME(3) NULL,
  end_reason  VARCHAR(32) NULL,
  PRIMARY KEY (id),
  KEY ix_sessions_machine (machine_id, started_at),
  -- Para cerrar por timeout las que quedaron abiertas.
  KEY ix_sessions_open (ended_at, started_at),
  CONSTRAINT fk_sessions_machine FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
