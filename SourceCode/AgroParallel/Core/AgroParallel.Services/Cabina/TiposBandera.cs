// ============================================================================
// TiposBandera.cs — que es lo que marco el operario: un arbol, un molino, una
// laguna... (idea del T-Wave de Sensor: puntos de interes con tipo).
//
// Hasta aca una bandera era un punto con color (roja/verde/amarilla) y notas.
// En el lote eso no alcanza: a la noche, o el que viene a sembrar al otro año,
// ve tres banderas rojas y no sabe cual es la piedra y cual el pozo. Con tipo,
// el mapa lo dice solo.
//
// Reglas:
//  · El CODIGO es lo que viaja al Flags.txt y a la API: estable, en minuscula,
//    sin comas ni espacios. El NOMBRE es solo de pantalla (pasa por Traductor).
//  · "Otro" = la bandera comun de siempre. Se guarda como VACIO: un lote sin
//    banderas tipadas escribe el archivo igual que la version anterior.
//  · Tipo vacio, null o desconocido (un archivo de una version futura) se lee
//    como "Otro". Nunca tira: es dato del operario, no se pierde por un tipo.
//
// Funcion PURA, solo System: la enlaza tambien PilotX.UI (mismo criterio que
// AvisosCabina) para que panel, mapa y motor usen los mismos codigos.
// ============================================================================

using System;
using System.Collections.Generic;

namespace AgroParallel.Cabina
{
    /// <summary>Un tipo de punto de interes.</summary>
    public sealed class TipoBandera
    {
        /// <summary>Codigo estable (archivo/API). "otro" = bandera comun.</summary>
        public string Codigo { get; private set; }

        /// <summary>Nombre para el operario, en castellano (clave del Traductor).</summary>
        public string Nombre { get; private set; }

        /// <summary>Letra del chip en la lista. El mapa GL no dibuja texto: ahí
        /// se distingue por forma + color. "" para Otro (va el punto de color).</summary>
        public string Letra { get; private set; }

        /// <summary>Color del icono en mapa y lista (#RRGGBB).</summary>
        public string ColorHex { get; private set; }

        /// <summary>Color viejo (0 roja, 1 verde, 2 amarilla) que se guarda junto
        /// al tipo: es lo que ve un lector que no conoce tipos.</summary>
        public int ColorLegado { get; private set; }

        public TipoBandera(string codigo, string nombre, string letra, string colorHex, int colorLegado)
        {
            Codigo = codigo;
            Nombre = nombre;
            Letra = letra;
            ColorHex = colorHex;
            ColorLegado = colorLegado;
        }
    }

    public static class TiposBandera
    {
        public const string Arbol  = "arbol";
        public const string Molino = "molino";
        public const string Agua   = "agua";
        public const string Tanque = "tanque";
        public const string Casa   = "casa";
        public const string Piedra = "piedra";
        public const string Otro   = "otro";

        private static readonly TipoBandera[] _todos =
        {
            new TipoBandera(Arbol,  "Árbol / monte",           "A", "#2E7D32", 1),
            new TipoBandera(Molino, "Molino / estructura",     "M", "#535E54", 2),
            new TipoBandera(Agua,   "Laguna / bañado / canal", "L", "#2B7BD6", 2),
            new TipoBandera(Tanque, "Tanque de agua",          "T", "#17A2B8", 2),
            new TipoBandera(Casa,   "Casa / galpón",           "C", "#A0522D", 2),
            new TipoBandera(Piedra, "Piedra / obstáculo",      "P", "#7A6A5C", 0),
            // Otro: el icono es la bandera de color de siempre, este color es
            // solo de referencia (el mapa usa el color elegido).
            new TipoBandera(Otro,   "Otro",                    "",  "#D9534F", 0),
        };

        /// <summary>Todos los tipos, en el orden de los botones de alta.</summary>
        public static IReadOnlyList<TipoBandera> Todos { get { return _todos; } }

        /// <summary>El tipo de un codigo. Vacio/null/desconocido → Otro.</summary>
        public static TipoBandera De(string codigo)
        {
            string c = Limpiar(codigo);
            for (int i = 0; i < _todos.Length; i++)
                if (_todos[i].Codigo == c) return _todos[i];
            return _todos[_todos.Length - 1];
        }

        /// <summary>
        /// Lo que se guarda: el codigo conocido en minuscula, o "" para la
        /// bandera comun (Otro, vacio, desconocido). Nunca devuelve null.
        /// </summary>
        public static string Normalizar(string codigo)
        {
            string c = De(codigo).Codigo;
            return c == Otro ? "" : c;
        }

        /// <summary>true si es una bandera comun (sin tipo propio).</summary>
        public static bool EsComun(string codigo)
        {
            return Normalizar(codigo).Length == 0;
        }

        private static string Limpiar(string codigo)
        {
            return string.IsNullOrWhiteSpace(codigo) ? "" : codigo.Trim().ToLowerInvariant();
        }
    }
}
