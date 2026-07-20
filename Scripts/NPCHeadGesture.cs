using System.Collections;
using UnityEngine;

namespace XNPCVoiceControl
{
    public enum HeadGestureType { None, Nod, Shake }

    /// <summary>
    /// Plays a brief head nod or shake by setting IdleVar on the avatar controller,
    /// then resets to neutral after HoldSeconds. Uses UpdateInt (not raw Animator.SetInt)
    /// because 7DTD overwrites direct animator calls each frame.
    /// </summary>
    public class NPCHeadGesture : MonoBehaviour
    {
        private HeadGestureConfig _cfg;
        private EntityAlive _entity;
        private Coroutine _holdRoutine;

        public void Init(EntityAlive entity, HeadGestureConfig cfg)
        {
            _entity = entity;
            _cfg    = cfg;
        }

        /// <summary>
        /// Play a nod or shake gesture. Safe to call repeatedly — cancels any in-progress hold.
        /// </summary>
        public void Play(HeadGestureType type)
        {
            if (_cfg == null || !_cfg.Enabled || type == HeadGestureType.None) return;

            var ac = _entity?.emodel?.avatarController;
            if (ac == null) return;

            int value = type == HeadGestureType.Nod ? _cfg.NodValue : _cfg.ShakeValue;
            if (_holdRoutine != null) StopCoroutine(_holdRoutine);

            ac.UpdateInt(_cfg.GestureParam, value, true);
            _holdRoutine = StartCoroutine(HoldThenReset(ac));
        }

        private IEnumerator HoldThenReset(AvatarController ac)
        {
            yield return new WaitForSeconds(_cfg.HoldSeconds);
            ac.UpdateInt(_cfg.GestureParam, _cfg.NeutralValue, true);
            _holdRoutine = null;
        }

        private void OnDestroy()
        {
            if (_holdRoutine != null) StopCoroutine(_holdRoutine);
        }
    }
}
