-- machine_interfaces: foto del estado actual, se reescribe cuando cambia.
-- machine_ip_history: el requisito explicito, intervalo cerrado.
--
-- CRITICO: solo se escribe historial cuando la IP CAMBIA. Con heartbeat cada
-- 30 s, escribir siempre serian 2.880 inserts/dia/PC de basura.

CREATE TABLE machine_interfaces (
  machine_id CHAR(36)     NOT NULL,
  name       VARCHAR(128) NOT NULL,
  ip         VARCHAR(45)  NOT NULL,
  mac        VARCHAR(17)  NULL,
  is_primary TINYINT(1)   NOT NULL DEFAULT 0,
  updated_at DATETIME(3)  NOT NULL,
  PRIMARY KEY (machine_id, name, ip),
  CONSTRAINT fk_interfaces_machine FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE machine_ip_history (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  machine_id CHAR(36)    NOT NULL,
  ip         VARCHAR(45) NOT NULL,
  mac        VARCHAR(17) NULL,
  valid_from DATETIME(3) NOT NULL,
  valid_to   DATETIME(3) NULL,
  PRIMARY KEY (id),
  KEY ix_iphist_machine (machine_id, valid_to),
  CONSTRAINT fk_iphist_machine FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
