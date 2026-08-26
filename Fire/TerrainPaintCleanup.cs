using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// Attached to a spawned "cultivate"-style piece as a safety net. Real
    /// Cultivator/Hoe use normally leaves nothing behind after the terrain
    /// paint applies, so this piece should self-destroy on its own — but if
    /// it doesn't, this force-cleans it after a short delay via the proven
    /// ZNetView.Destroy() path, never the risky direct ZNetScene.Destroy(go)
    /// call that caused real corruption earlier in this project's history.
    /// </summary>
    public class TerrainPaintCleanup : MonoBehaviour
    {
        private float _deadline;

        private void Start()
        {
            _deadline = Time.time + 5f;
        }

        private void Update()
        {
            if (Time.time < _deadline) return;

            ValheimBridge.ForceCleanupTerrainPaintPiece(gameObject);
            Destroy(this);
        }
    }
}
