# Guía de Usuario — Sistema Municipal LPR/ANPR

> Audiencia: operadores de C4/C5, supervisores, patrullas móviles y auditores forenses. Describe cómo se usa el sistema **según el diseño aprobado**.
>
> **Estado actual:** el sistema todavía no tiene interfaz de usuario (dashboard web ni app móvil) — esta guía describe el funcionamiento previsto una vez completadas las Fases 2 y 3. Hoy solo existe el backend (autenticación, autorización y modelo de datos). Se actualizará esta guía a medida que cada pantalla/flujo quede realmente construido.

## 1. ¿Qué hace el sistema?

Detecta automáticamente las matrículas de los vehículos que pasan por arcos de seguridad o avenidas de alta velocidad, y las compara al instante contra la Lista Negra de Vehículos con Reporte de Robo. Si hay coincidencia, se genera una alerta que llega en menos de un segundo a:
- Las pantallas del Centro de Mando (C4/C5).
- La aplicación de las patrullas en la zona.

## 2. Roles y qué puede hacer cada uno

El acceso se controla por rol; cada usuario recibe sus credenciales y rol asignado por un administrador del sistema (vía Keycloak, el proveedor de identidad).

| Rol | Puede hacer |
|---|---|
| **SuperAdmin** | Todo: gestionar alertas, cámaras, la Lista Negra, usuarios, y consultar el histórico de lecturas |
| **SupervisorC4** | Ver y gestionar alertas, cámaras y la Lista Negra; consultar usuarios |
| **OperadorC4** | Ver alertas, cámaras y la Lista Negra (sin editar) |
| **PatrullaMovil** | Ver las alertas relevantes a su zona |
| **AuditorForense** | Ver alertas, cámaras, la Lista Negra y el histórico completo de lecturas (para investigaciones) — sin editar nada |

Si intentas una acción que tu rol no permite, el sistema la rechaza (no rompe nada, simplemente no te deja).

## 3. Cómo se inicia sesión

El inicio de sesión es centralizado (Keycloak): un único usuario y contraseña sirven tanto para el dashboard web de C4 como para la aplicación móvil de patrullas. El sistema soporta autenticación multifactor (MFA) para roles con acceso a datos sensibles.

## 4. Qué significa una alerta

Cuando una cámara detecta una placa que coincide con la Lista Negra, la alerta muestra:
- La placa detectada y la cámara/ubicación donde ocurrió.
- La hora exacta.
- Una foto de la placa.
- El nivel de la alerta (crítica para coincidencias confirmadas).

Un **Operador C4** o **Supervisor C4** puede marcar la alerta como atendida o descartada; ese cambio queda registrado (quién y cuándo) para auditoría — el registro original de la lectura nunca se modifica ni se borra.

## 5. Mapa de cámaras

Cada cámara tiene una ubicación geográfica registrada (compatible con ESRI/Google Maps). El dashboard C4 mostrará las cámaras y las alertas sobre un mapa una vez construida esa pantalla (Fase 2/3).

## 6. Qué hacer si algo no funciona

Por ahora, ante cualquier duda o comportamiento inesperado, contactar al equipo técnico del proyecto — no hay todavía un canal de soporte interno propio del sistema (se documentará aquí cuando exista).
