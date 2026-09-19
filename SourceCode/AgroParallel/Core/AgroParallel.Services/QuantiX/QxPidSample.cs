// ============================================================================
// QxPidSample.cs — una muestra del registro de PID, de un motor, en un tick.
//
// Struct a proposito: el bridge arma 14 de estas cada 200 ms y las encola. Sin
// asignaciones en el heap no hay presion de GC en el lazo que comanda motores.
// ============================================================================

namespace AgroParallel.QuantiX
{
    public struct QxPidSample
    {
        /// <summary>UID del nodo. Va en el NOMBRE del archivo, no en cada fila:
        /// MotorIdx solo es unico dentro de su nodo.</summary>
        public string Uid;

        /// <summary>Indice del motor DENTRO del nodo (0..N-1 de nodo.Motores).</summary>
        public int MotorIdx;

        /// <summary>Nombre que le puso el operario. Va al sidecar, no al CSV.</summary>
        public string Nombre;

        /// <summary>RPM medidas por el encoder. null = el nodo no publica (sin
        /// dato). Cero = el motor esta quieto. NO son lo mismo.</summary>
        public double? RpmReal;

        /// <summary>RPM que PilotX le pide al motor.</summary>
        public double RpmTarget;

        public double PpsReal;
        public double PpsTarget;
        public int Pwm;

        /// <summary>PWM sobre 4095, en %. 100 = saturado: el motor no puede
        /// seguir al target y el problema no es de ganancias.</summary>
        public int LoadPct;

        /// <summary>Velocidad de las secciones que cubre ESTE motor. En curva no
        /// es la del tractor (ver MotorSpeedKmh en QuantiXMotorBridge).</summary>
        public double VelMotorKmh;

        /// <summary>Velocidad del tractor segun GPS.</summary>
        public double VelGpsKmh;

        /// <summary>Dosis objetivo resuelta (manual > mapa > fija).</summary>
        public double Dosis;

        public bool SeccionOn;
    }
}
