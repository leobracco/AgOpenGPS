// ============================================================================
// PerfilGuard.cs — protección de perfiles de vehículo con clave.
// Un perfil protegido tiene un sidecar "<nombre>.clave" al lado del XML en
// Vehicles/ con el SHA-256 (hex) de la clave elegida por el operario al
// protegerlo. Sin la clave no se puede borrar ni sobrescribir ese perfil.
// Lo usan FormGpsPerfilService (página HTML) y las forms nativas legacy
// (FormLoadProfile borrar / FormNewProfile sobrescribir) para que no haya
// bypass por ningún camino.
// ============================================================================

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgroParallel.Adapters
{
    public static class PerfilGuard
    {
        public const string Extension = ".clave";

        public static string SidecarPath(string vehiclesDirectory, string nombre)
        {
            return Path.Combine(vehiclesDirectory, nombre + Extension);
        }

        public static bool EstaProtegido(string vehiclesDirectory, string nombre)
        {
            try { return File.Exists(SidecarPath(vehiclesDirectory, nombre)); }
            catch { return false; }
        }

        /// <summary>true si el perfil NO está protegido o la clave coincide.</summary>
        public static bool VerificarClave(string vehiclesDirectory, string nombre, string clave)
        {
            try
            {
                string sidecar = SidecarPath(vehiclesDirectory, nombre);
                if (!File.Exists(sidecar)) return true;
                string guardado = File.ReadAllText(sidecar).Trim();
                return !string.IsNullOrEmpty(guardado) &&
                       string.Equals(guardado, Hash(clave), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void Proteger(string vehiclesDirectory, string nombre, string clave)
        {
            File.WriteAllText(SidecarPath(vehiclesDirectory, nombre), Hash(clave));
        }

        public static void Desproteger(string vehiclesDirectory, string nombre)
        {
            string sidecar = SidecarPath(vehiclesDirectory, nombre);
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }

        public static string Hash(string clave)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(clave ?? ""));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
