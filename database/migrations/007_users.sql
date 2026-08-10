-- Usuarios del dashboard. Los roles completos se aplican en Fase 13; aqui solo
-- se necesita autenticar el WPF.
--
-- Deliberadamente SIN usuario sembrado con password fijo: el servidor crea el
-- admin en el primer arranque con una password aleatoria y la escribe UNA VEZ
-- en el log. Un default hardcodeado es exactamente lo que nadie cambia despues.

CREATE TABLE users (
  id            INT UNSIGNED NOT NULL AUTO_INCREMENT,
  username      VARCHAR(64)  NOT NULL,
  password_hash VARCHAR(255) NOT NULL,
  role          ENUM('viewer','technician','engineer','administrator') NOT NULL DEFAULT 'viewer',
  is_active     TINYINT(1)   NOT NULL DEFAULT 1,
  created_at    DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_users_username (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
