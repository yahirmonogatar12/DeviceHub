-- Dar de baja una PC.
--
-- BAJA Y NO BORRADO, y no es una preferencia: siete tablas cuelgan de machines
-- con ON DELETE CASCADE, y entre ellas esta machine_sessions -- el registro de
-- quien controlo esa maquina, cuando y desde donde. Un DELETE se lo lleva todo
-- por delante, y en un sistema cuyo motivo de existir es responder "quien toco
-- esa PC", eso convierte una limpieza de la lista en la perdida de la prueba.
--
-- La fila se queda con su historial entero. Lo que se va es el token, que es lo
-- unico que de verdad hace falta para que esa PC no vuelva a conectarse.

ALTER TABLE machines
  ADD COLUMN retired_at DATETIME(3) NULL AFTER identity_state,
  ADD COLUMN retired_by VARCHAR(120) NULL AFTER retired_at;

-- Se filtra por "las de baja no" en cada listado, que es la consulta mas comun
-- del dashboard.
CREATE INDEX ix_machines_retired ON machines (retired_at);
