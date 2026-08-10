-- Fase 12: auditoria de acciones DE USUARIO.
--
-- Distinta de machine_events, que registra lo que hace el sistema (conflictos de
-- identidad, cambios de hardware). Aqui va lo que hizo una persona.
--
-- SIN FOREIGN KEY a machines, y a proposito. Todas las demas tablas hijas tienen
-- ON DELETE CASCADE, asi que borrar una maquina se lleva sus metricas e
-- historiales -- correcto para datos operativos. Para la auditoria seria
-- destruir la prueba de quien hizo que: bastaria con borrar el equipo para
-- borrar el rastro. Por eso se guardan machine_code y site_code como COPIA de
-- texto: la fila sobrevive aunque la maquina desaparezca.
--
-- Tampoco tiene purga. La retencion de metricas existe porque nadie consulta el
-- CPU de hace ocho meses; la auditoria es exactamente lo contrario.

CREATE TABLE machine_audit (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  occurred_at  DATETIME(3) NOT NULL,
  user_id      VARCHAR(64) NOT NULL,
  user_role    VARCHAR(32) NULL,
  action       VARCHAR(48) NOT NULL,
  machine_id   CHAR(36)    NULL,
  machine_code VARCHAR(64) NULL,
  site_code    VARCHAR(32) NULL,
  -- Correlaciona todo lo que ocurrio en una misma llamada.
  request_id   VARCHAR(64) NULL,
  source_ip    VARCHAR(45) NULL,
  -- 'denied' es tan importante como 'allowed': un intento rechazado de apagar
  -- 30 PCs es justo lo que hay que poder ver despues.
  outcome      ENUM('allowed','denied','failed') NOT NULL DEFAULT 'allowed',
  details      TEXT        NULL,
  PRIMARY KEY (id),
  KEY ix_audit_time (occurred_at),
  KEY ix_audit_machine (machine_id, occurred_at),
  KEY ix_audit_user (user_id, occurred_at),
  KEY ix_audit_action (action, occurred_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
