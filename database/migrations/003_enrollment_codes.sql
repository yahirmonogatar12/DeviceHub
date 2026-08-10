-- Fase 1.3 / 1.4: codigos de un solo uso. Nunca un token compartido dentro del
-- instalador (un instalador viejo filtrado no puede registrar maquinas).
--
--   target_machine_id NULL  -> enrollment: crea una maquina nueva
--   target_machine_id != NULL -> recovery: repone token y pines de una maquina
--                                existente, conservando identidad e historial.
--
-- El codigo se guarda solo hasheado; se muestra en claro una unica vez.

CREATE TABLE enrollment_codes (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code_hash          CHAR(64)     NOT NULL,
  site_id            INT UNSIGNED NOT NULL,
  created_by         VARCHAR(64)  NOT NULL,
  created_at         DATETIME(3)  NOT NULL,
  expires_at         DATETIME(3)  NOT NULL,
  max_uses           INT          NOT NULL DEFAULT 1,
  used_count         INT          NOT NULL DEFAULT 0,
  target_machine_id  CHAR(36)     NULL,
  used_by_machine_id CHAR(36)     NULL,
  used_at            DATETIME(3)  NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_enrollment_code_hash (code_hash),
  CONSTRAINT fk_enrollment_site FOREIGN KEY (site_id) REFERENCES sites (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
