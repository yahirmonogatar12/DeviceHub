-- Fase 6: monitoreo.
--
-- Granularidad de UN MINUTO, no de un segundo. El agente muestrea cada 5 s y
-- agrega localmente; aqui llega ya resumido. A 200 PCs, una fila por segundo
-- serian 17 millones de filas al dia.
--
-- La PK compuesta hace el reenvio idempotente: si el agente reconecta y vuelve a
-- mandar un minuto que ya estaba, se sobreescribe en vez de duplicarse.

CREATE TABLE machine_metrics (
  machine_id            CHAR(36)    NOT NULL,
  minute_utc            DATETIME    NOT NULL,
  cpu_avg               FLOAT       NULL,
  cpu_max               FLOAT       NULL,
  memory_avg            FLOAT       NULL,
  memory_max            FLOAT       NULL,
  disk_min_free_percent FLOAT       NULL,
  net_rx_bytes_per_sec  BIGINT      NULL,
  net_tx_bytes_per_sec  BIGINT      NULL,
  PRIMARY KEY (machine_id, minute_utc),
  -- Indice para la purga por antiguedad, que barre por fecha y no por maquina.
  KEY ix_metrics_minute (minute_utc),
  CONSTRAINT fk_metrics_machine FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Ultima medicion, desnormalizada en machines para que el listado del dashboard
-- siga siendo un SELECT plano sin junta contra una tabla de series temporales.
--
-- Esto NO contradice la regla de "el estado no se almacena": el estado
-- online/offline se deriva de last_seen sin costo; un porcentaje de CPU medido
-- no se deriva de nada, es una observacion.
ALTER TABLE machines
  ADD COLUMN cpu_percent       FLOAT       NULL AFTER uptime_seconds,
  ADD COLUMN memory_percent    FLOAT       NULL AFTER cpu_percent,
  ADD COLUMN disk_free_percent FLOAT       NULL AFTER memory_percent,
  ADD COLUMN metrics_at        DATETIME(3) NULL AFTER disk_free_percent;
