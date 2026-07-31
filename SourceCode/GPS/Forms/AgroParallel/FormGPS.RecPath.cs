// ============================================================================
// FormGPS.RecPath.cs — puente de FormGPS al manejador de caminos grabados.
//
// La lógica ya NO vive acá: se movió a AgOpenGPS.Core/Classes/RecPathManager.cs
// (/api/recpath daba 404 contra el motor headless). Delegación fina mientras
// WinForms exista (ver docs/RETIRAR-WINFORMS.md).
// ============================================================================

using System.Collections.Generic;
using System.IO;

namespace AgOpenGPS
{
    public partial class FormGPS
    {
        private RecPathManager _recPathMgr;

        /// (Sin `??=`: este proyecto compila en C# 7.3.)
        private RecPathManager RecPathMgr
        {
            get
            {
                if (_recPathMgr == null)
                {
                    _recPathMgr = new RecPathManager(
                        recPath,
                        dirLote: () => string.IsNullOrEmpty(currentFieldDirectory)
                            ? null
                            : Path.Combine(RegistrySettings.fieldsDirectory, currentFieldDirectory),
                        guardar: () => FileSaveRecPath(),
                        guardarComo: n => FileSaveRecPath(n),
                        ocultarPanel: () => panelDrag.Visible = false);
                }
                return _recPathMgr;
            }
        }

        internal List<string> RecPath_ListFiles() => RecPathMgr.ListFiles();
        internal bool RecPath_Load(string name) => RecPathMgr.Load(name);
        internal bool RecPath_Delete(string name) => RecPathMgr.Delete(name);
        internal void RecPath_TurnOff() => RecPathMgr.TurnOff();
        internal bool RecPath_SaveWithName(string name) => RecPathMgr.SaveWithName(name);
        internal void RecPath_Discard() => RecPathMgr.Discard();
    }
}
