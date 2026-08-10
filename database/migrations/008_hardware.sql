-- Fase 5: inventario de hardware. Una fila por maquina con el estado ACTUAL.
--
-- No hay tabla de historial de hardware: cuando el hash cambia se escribe un
-- evento HARDWARE_CHANGED en machine_events con el detalle. Eso da la trazabilidad
-- (se le subio RAM, se le cambio el disco) sin una segunda tabla que nadie
-- consultaria relacionalmente.
--
-- `disks` va en JSON por lo mismo: es una lista que se lee entera o no se lee.

CREATE TABLE machine_hardware (
  machine_id         CHAR(36)     NOT NULL,
  hash               CHAR(64)     NOT NULL,
  cpu_model          VARCHAR(160) NULL,
  cpu_cores          INT          NULL,
  cpu_threads        INT          NULL,
  total_memory_bytes BIGINT       NULL,
  gpu_model          VARCHAR(160) NULL,
  motherboard        VARCHAR(160) NULL,
  bios_version       VARCHAR(120) NULL,
  bios_serial        VARCHAR(120) NULL,
  os_caption         VARCHAR(160) NULL,
  os_version         VARCHAR(60)  NULL,
  os_build           VARCHAR(60)  NULL,
  disks              JSON         NULL,
  collected_at       DATETIME(3)  NOT NULL,
  updated_at         DATETIME(3)  NOT NULL,
  PRIMARY KEY (machine_id),
  CONSTRAINT fk_hardware_machine FOREIGN KEY (machine_id) REFERENCES machines (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
