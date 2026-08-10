-- Fase 13: gestion de usuarios.
--
-- Hasta aqui la matriz de roles existia en el codigo pero no habia forma de
-- crear un technician: solo existia el admin que el servidor genera al arrancar.
-- Una matriz de permisos con un unico usuario administrador no protege nada.

ALTER TABLE users
  ADD COLUMN created_by    VARCHAR(64) NULL AFTER role,
  ADD COLUMN last_login_at DATETIME(3) NULL AFTER created_by,
  ADD COLUMN updated_at    DATETIME(3) NULL AFTER last_login_at;

-- Pines SPKI que cada agente reporta tener cargados.
--
-- El heartbeat ya los enviaba pero se descartaban, y sin guardarlos la rotacion
-- de certificado es a ciegas: el paso 2 del procedimiento consiste precisamente
-- en esperar a que TODAS las maquinas confirmen el pin nuevo antes de cambiar
-- el certificado. Sin este dato no hay forma de saber cuando es seguro.
ALTER TABLE machines
  ADD COLUMN pinned_keys TEXT NULL AFTER remote_available;
