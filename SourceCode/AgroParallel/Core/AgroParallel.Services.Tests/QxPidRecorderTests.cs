// ============================================================================
// QxPidRecorderTests.cs — registro de PID de QuantiX.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AgroParallel.Models;
using AgroParallel.QuantiX;
using NUnit.Framework;

namespace AgroParallel.Services.Tests
{
    [TestFixture]
    public class QxPidRecorderTests
    {
        [Test]
        public void MotorLive_expone_load_pct()
        {
            var m = new MotorLive { Id = 0, LoadPct = 86 };
            Assert.That(m.LoadPct, Is.EqualTo(86));
        }

        private static string DirTemp()
        {
            string d = Path.Combine(Path.GetTempPath(), "qxpid_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }


        /// <summary>Lee el CSV MIENTRAS el recorder lo tiene abierto, igual que
        /// hace Excel cuando el operario lo mira sin cerrar PilotX. File.ReadAllLines
        /// no sirve aca: pide FileShare.Read y choca con el writer.</summary>
        private static string[] LeerLineas(string path)
        {
            var lineas = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                string l;
                while ((l = sr.ReadLine()) != null) lineas.Add(l);
            }
            return lineas.ToArray();
        }

        private static QxPidSample Muestra(string uid, int idx)
        {
            return new QxPidSample
            {
                Uid = uid, MotorIdx = idx, Nombre = "Cuerpo 1",
                RpmReal = 44, RpmTarget = 45,
                PpsReal = 15.0, PpsTarget = 15.2,
                Pwm = 2050, LoadPct = 50,
                VelMotorKmh = 6.2, VelGpsKmh = 6.2,
                Dosis = 7.0, SeccionOn = true
            };
        }

        [Test]
        public void Escribe_un_csv_por_motor_con_uid_en_el_nombre()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.Registrar(Muestra("A4CF12AB9E30", 1));
            rec.Registrar(Muestra("7B2E0091CC14", 0));
            rec.FlushAhora();

            string sesion = rec.SesionDir;
            Assert.That(File.Exists(Path.Combine(sesion, "A4CF12AB9E30_m0.csv")), Is.True);
            Assert.That(File.Exists(Path.Combine(sesion, "A4CF12AB9E30_m1.csv")), Is.True);
            Assert.That(File.Exists(Path.Combine(sesion, "7B2E0091CC14_m0.csv")), Is.True);
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }

        [Test]
        public void La_cabecera_y_la_fila_salen_en_el_orden_declarado()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.FlushAhora();

            string[] lineas = LeerLineas(Path.Combine(rec.SesionDir, "A4CF12AB9E30_m0.csv"));
            Assert.That(lineas[0], Is.EqualTo(
                "t_s,rpm_real,rpm_target,pps_real,pps_target,pwm,load_pct,vel_motor,vel_gps,dosis,sec_on,marca"));
            string[] c = lineas[1].Split(',');
            Assert.That(c.Length, Is.EqualTo(12));
            Assert.That(c[1], Is.EqualTo("44"));
            Assert.That(c[2], Is.EqualTo("45"));
            Assert.That(c[5], Is.EqualTo("2050"));
            Assert.That(c[6], Is.EqualTo("50"));
            Assert.That(c[10], Is.EqualTo("1"));
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }

        [Test]
        public void Nodo_caido_escribe_vacio_no_cero()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            var m = Muestra("A4CF12AB9E30", 0);
            m.RpmReal = null;
            rec.Registrar(m);
            rec.FlushAhora();

            string[] lineas = LeerLineas(Path.Combine(rec.SesionDir, "A4CF12AB9E30_m0.csv"));
            string[] c = lineas[1].Split(',');
            Assert.That(c[1], Is.EqualTo(""), "cero significa motor quieto, vacio significa sin dato");
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }

        [Test]
        public void Los_decimales_van_con_punto_aunque_el_locale_use_coma()
        {
            var previo = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("es-AR");
            try
            {
                string dir = DirTemp();
                var rec = new QxPidRecorder(dir);
                rec.AbrirSesion();
                rec.Registrar(Muestra("A4CF12AB9E30", 0));
                rec.FlushAhora();

                string[] lineas = LeerLineas(Path.Combine(rec.SesionDir, "A4CF12AB9E30_m0.csv"));
                Assert.That(lineas[1], Does.Contain("6.20"));
                Assert.That(lineas[1].Split(',').Length, Is.EqualTo(12));
                rec.CerrarSesion();
                Directory.Delete(dir, true);
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = previo; }
        }

        [Test]
        public void Sesion_sin_muestras_no_deja_carpeta_huerfana()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            string sesion = rec.SesionDir;
            rec.CerrarSesion();
            Assert.That(Directory.Exists(sesion), Is.False);
            Directory.Delete(dir, true);
        }

        [Test]
        public void Registrar_no_explota_si_el_directorio_no_existe()
        {
            var rec = new QxPidRecorder(Path.Combine(Path.GetTempPath(),
                "qxpid_inexistente_" + Guid.NewGuid().ToString("N"), "sub", "sub"));
            rec.AbrirSesion();
            Assert.DoesNotThrow(() => rec.Registrar(Muestra("A4CF12AB9E30", 0)));
            Assert.DoesNotThrow(() => rec.FlushAhora());
        }
    }
}
