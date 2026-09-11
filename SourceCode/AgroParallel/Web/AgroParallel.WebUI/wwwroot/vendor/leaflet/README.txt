Leaflet — vendoring local (sin CDN)
====================================

La página /pages/mapas.html depende de Leaflet 1.9.x vendorizado en esta
carpeta. NO referenciar CDN (regla del proyecto: kiosko offline-first).

Pasos para instalar la primera vez (o al actualizar versión):

1. Descargar el bundle oficial:
     https://leafletjs.com/download.html
   Versión recomendada: 1.9.4 (estable, ~150 KB minificada).

2. Descomprimir y copiar a esta carpeta:

     vendor/leaflet/
       ├── leaflet.js          (minificado)
       ├── leaflet.css
       └── images/
           ├── marker-icon.png
           ├── marker-icon-2x.png
           ├── marker-shadow.png
           └── layers.png  (opcional, solo si se usa L.control.layers visual)

3. Verificar que abre /pages/mapas.html y se ve el mapa sin errores de consola.

Notas:

- Las imágenes de marker NO son necesarias para nuestro uso actual (solo
  pintamos polígonos, líneas y círculos vectoriales). Igual conviene tener
  marker-icon{,-2x,-shadow}.png para no levantar 404 si en el futuro alguien
  agrega un L.marker() default.

- Si en algún momento se necesitan tiles satelitales (modo "online"), se
  agregará un toggle en la UI que llame a L.tileLayer(...). Hoy NO se hace
  para mantener el kiosko 100% offline.

- Build script: si el build pipeline genera el ZIP de release, asegurar que
  esta carpeta queda incluida (es parte de wwwroot, por lo que ya está dentro
  del output bin/Release/.../wwwroot/vendor/leaflet/).
