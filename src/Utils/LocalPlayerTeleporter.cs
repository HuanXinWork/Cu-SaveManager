using UnityEngine;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>同步移动本地玩家身体与主相机并归零刚体速度，防止传送后残留惯性或镜头脱节。</summary>
    internal static class LocalPlayerTeleporter
    {
        internal static bool TryMoveTo(Vector3 pos, bool forceZeroZ = false)
        {
            var cam = PlayerCamera.main;
            if (cam == null || cam.body == null) return false;
            cam.body.transform.position = forceZeroZ ? new Vector3(pos.x, pos.y, 0f) : pos;
            cam.transform.position = new Vector3(pos.x, pos.y, -10f);
            if (cam.body.rb != null) cam.body.rb.velocity = Vector2.zero;
            return true;
        }
    }
}
