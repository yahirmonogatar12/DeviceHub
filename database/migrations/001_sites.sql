-- Fase 3: jerarquia de ubicacion. Existe desde el dia 1 aunque haya una sola
-- planta: asumir que area+line+station es globalmente unico sale caro de deshacer.
-- El timezone es por sitio para poder mostrar un evento en hora DE LA PLANTA,
-- teniendo todo almacenado en UTC.

CREATE TABLE sites (
  id         INT UNSIGNED NOT NULL AUTO_INCREMENT,
  code       VARCHAR(32)  NOT NULL,
  name       VARCHAR(128) NOT NULL,
  timezone   VARCHAR(64)  NOT NULL DEFAULT 'America/Monterrey',
  created_at DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_sites_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT INTO sites (code, name, timezone, created_at)
VALUES ('ILSAN-MTY', 'ILSAN Monterrey', 'America/Monterrey', UTC_TIMESTAMP(3));
