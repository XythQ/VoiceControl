namespace XNPCVoiceControl
{
    public class HeadGestureConfig
    {
        public bool   Enabled      = true;
        public string GestureParam = "IdleVar";
        public int    NodValue     = 1;
        public int    ShakeValue   = 2;
        public int    NeutralValue = 0;
        public float  HoldSeconds  = 1.5f;
    }
}
