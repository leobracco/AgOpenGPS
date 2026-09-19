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


        private static string SembrarSesiones(string dir, int cuantas)
        {
            string raiz = Path.Combine(dir, "pid-quantix");
            Directory.CreateDirectory(raiz);
            for (int i = 0; i < cuantas; i++)
            {
                string d = Path.Combine(raiz, "2026-09-01_" + i.ToString("00") + "00");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "x.csv"), "x");
            }
            return raiz;
        }

        [Test]
        public void Purga_deja_las_ultimas_veinte_sesiones()
        {
            string dir = DirTemp();
            string raiz = SembrarSesiones(dir, 25);

            var rec = new QxPidRecorder(dir);
            rec.Purgar();

            Assert.That(Directory.GetDirectories(raiz).Length, Is.EqualTo(QxPidRecorder.MaxSesiones));
            Assert.That(Directory.Exists(Path.Combine(raiz, "2026-09-01_0000")), Is.False, "la mas vieja se borra");
            Assert.That(Directory.Exists(Path.Combine(raiz, "2026-09-01_2400")), Is.True, "la mas nueva queda");
            Directory.Delete(dir, true);
        }

        [Test]
        public void Purga_nunca_borra_una_sesion_marcada()
        {
            string dir = DirTemp();
            string raiz = SembrarSesiones(dir, 25);
            File.WriteAllText(Path.Combine(raiz, "2026-09-01_0000", ".marcada"), "kp=3");

            var rec = new QxPidRecorder(dir);
            rec.Purgar();

            Assert.That(Directory.Exists(Path.Combine(raiz, "2026-09-01_0000")), Is.True,
                "si alguien la marco, esa corrida importa");
            Directory.Delete(dir, true);
        }

        [Test]
        public void Marcar_deja_el_centinela_en_la_sesion()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.Marcar("kp=3");
            rec.FlushAhora();
            Assert.That(File.Exists(Path.Combine(rec.SesionDir, ".marcada")), Is.True);
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }


        [Test]
        public void Resumen_calcula_desvio_y_tiempo_saturado()
        {
            var ms = new List<QxPidSample>();
            for (int i = 0; i < 10; i++)
            {
                var m = Muestra("A4CF12AB9E30", 1);
                m.RpmReal = (i < 5) ? 40 : 50;       // promedio 45, desvio 5
                m.RpmTarget = 45;
                m.LoadPct = (i < 3) ? 100 : 80;      // 30% del tiempo saturado
                ms.Add(m);
            }

            var r = QxPidResumen.Calcular(ms);
            Assert.That(r.RpmProm, Is.EqualTo(45).Within(0.01));
            Assert.That(r.RpmDesvio, Is.EqualTo(5).Within(0.01));
            Assert.That(r.TiempoSaturadoPct, Is.EqualTo(30).Within(0.01));
            Assert.That(r.LoadPromPct, Is.EqualTo(86).Within(0.01));
        }

        [Test]
        public void Resumen_ignora_las_muestras_sin_dato()
        {
            var ms = new List<QxPidSample>();
            var a = Muestra("A4CF12AB9E30", 0); a.RpmReal = 40; ms.Add(a);
            var b = Muestra("A4CF12AB9E30", 0); b.RpmReal = null; ms.Add(b);
            var c = Muestra("A4CF12AB9E30", 0); c.RpmReal = 50; ms.Add(c);

            var r = QxPidResumen.Calcular(ms);
            Assert.That(r.RpmProm, Is.EqualTo(45).Within(0.01), "la muestra sin dato no promedia como cero");
        }

        [Test]
        public void La_sesion_deja_el_sidecar_json()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.FlushAhora();
            string sesion = rec.SesionDir;
            rec.CerrarSesion();

            string json = File.ReadAllText(Path.Combine(sesion, "sesion.json"));
            Assert.That(json, Does.Contain("\"motores\""));
            Assert.That(json, Does.Contain("A4CF12AB9E30"));
            Directory.Delete(dir, true);
        }

        [Test]
        public void El_sidecar_guarda_las_ganancias_con_las_que_corrio()
        {
            string dir = DirTemp();
            var cfg = new MotoresConfig();
            cfg.Nodos.Add(new QxNodoConfig
            {
                Uid = "A4CF12AB9E30",
                Motores = new[] { new QxMotorConfig { Nombre = "Cuerpo 1", Kp = 2, Ki = 30, Kd = 0 } }
            });

            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion(cfg);
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.FlushAhora();
            string sesion = rec.SesionDir;
            rec.CerrarSesion();

            string json = File.ReadAllText(Path.Combine(sesion, "sesion.json"));
            Assert.That(json, Does.Contain("\"kp\": 2.00"),
                "sin las ganancias no se puede comparar una corrida con otra");
            Directory.Delete(dir, true);
        }

        [Test]
        public void El_buffer_rodante_guarda_las_ultimas_300_muestras()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            for (int i = 0; i < 400; i++)
            {
                var m = Muestra("A4CF12AB9E30", 0);
                m.RpmReal = i;
                rec.Registrar(m);
            }
            rec.FlushAhora();

            var buf = rec.BufferDe("A4CF12AB9E30", 0);
            Assert.That(buf.Count, Is.EqualTo(QxPidRecorder.MaxBuffer));
            Assert.That(buf[buf.Count - 1].RpmReal.Value, Is.EqualTo(399), "la ultima es la mas nueva");
            Assert.That(buf[0].RpmReal.Value, Is.EqualTo(100), "las viejas se descartan");
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }

        [Test]
        public void Motores_lista_lo_que_se_vio_en_la_sesion()
        {
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            rec.Registrar(Muestra("A4CF12AB9E30", 0));
            rec.Registrar(Muestra("7B2E0091CC14", 1));
            rec.FlushAhora();

            var ms = rec.Motores();
            Assert.That(ms.Count, Is.EqualTo(2));
            rec.CerrarSesion();
            Directory.Delete(dir, true);
        }


        [Test]
        public void Las_muestras_de_un_mismo_flush_no_comparten_el_tiempo()
        {
            // El t_s se sellaba en el flush, que corre 1 vez por segundo: las 5
            // muestras de ese segundo salian todas con el MISMO t_s y el eje
            // quedaba en escalones de 1 s, justo la resolucion de 5 Hz que hace
            // falta para ver oscilar el PID.
            string dir = DirTemp();
            var rec = new QxPidRecorder(dir);
            rec.AbrirSesion();
            for (int i = 0; i < 5; i++)
            {
                rec.Registrar(Muestra("A4CF12AB9E30", 0));
                System.Threading.Thread.Sleep(60);
            }
            rec.FlushAhora();

            string[] lineas = LeerLineas(Path.Combine(rec.SesionDir, "A4CF12AB9E30_m0.csv"));
            var tiempos = new List<string>();
            for (int i = 1; i < lineas.Length; i++) tiempos.Add(lineas[i].Split(',')[0]);

            Assert.That(tiempos.Count, Is.EqualTo(5));
            Assert.That(new HashSet<string>(tiempos).Count, Is.GreaterThan(1),
                "las 5 muestras salieron con el mismo t_s: el sello se esta tomando en el flush");
            rec.CerrarSesion();
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
