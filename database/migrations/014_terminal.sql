-- Fase 15: terminal remoto.
--
-- Se REUSA machine_sessions en vez de crear terminal_sessions: una sesion de
-- control remoto y una de terminal son la misma pregunta -- quien estuvo dentro
-- de esta maquina, cuando y desde donde. Dos tablas casi identicas obligarian a
-- unirlas con UNION cada vez que alguien quiera esa respuesta.

ALTER TABLE machine_sessions
  ADD COLUMN kind ENUM('remote','terminal') NOT NULL DEFAULT 'remote' AFTER machine_id;

-- Cada comando con su salida. Es lo que convierte "alguien tuvo un terminal
-- abierto" en "esto fue exactamente lo que ejecuto".
CREATE TABLE terminal_commands (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  session_id   CHAR(36)    NOT NULL,
  sequence     INT         NOT NULL,
  command      TEXT        NOT NULL,
  working_dir  VARCHAR(400) NULL,
  output       MEDIUMTEXT  NULL,
  exit_code    INT         NULL,
  truncated    TINYINT(1)  NOT NULL DEFAULT 0,
  started_at   DATETIME(3) NOT NULL,
  duration_ms  INT         NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_terminal_sequence (session_id, sequence),
  KEY ix_terminal_session (session_id, started_at),
  CONSTRAINT fk_terminal_session FOREIGN KEY (session_id) REFERENCES machine_sessions (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
