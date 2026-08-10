-- Fase 7: comandos.
--
-- Se persisten enteros, no solo para reporteria: `id` es la clave de
-- idempotencia. Si el agente reconecta y recibe un comando que ya ejecuto,
-- devuelve el resultado guardado en vez de volver a ejecutarlo. Sin eso, una
-- reconexion en mal momento provoca dos reinicios.
--
-- `expires_at` es la proteccion de verdad: una PC apagada dos horas no debe
-- ejecutar al reconectar un reinicio que se pidio hace dos horas.

CREATE TABLE machine_commands (
  id              CHAR(36)    NOT NULL,
  machine_id      CHAR(36)    NOT NULL,
  command_type    VARCHAR(48) NOT NULL,
  parameters_json JSON        NULL,
  requested_by    VARCHAR(64) NOT NULL,
  requested_at    DATETIME(3) NOT NULL,
  expires_at      DATETIME(3) NOT NULL,
  sent_at         DATETIME(3) NULL,
  started_at      DATETIME(3) NULL,
  completed_at    DATETIME(3) NULL,
  status          ENUM('pending','sent','running','completed','failed','expired','cancelled')
                  NOT NULL DEFAULT 'pending',
  result          TEXT        NULL,
  error_code      VARCHAR(64) NULL,
  agent_version   VARCHAR(32) NULL,
  PRIMARY KEY (id),
  -- Historial por maquina en el dashboard.
  KEY ix_commands_machine (machine_id, requested_at),
  -- Pendientes a entregar al reconectar, y barrido de vencidos.
  KEY ix_commands_pending (machine_id, status, expires_at),
  CONSTRAINT fk_commands_machine FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
