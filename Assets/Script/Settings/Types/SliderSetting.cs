using System;
using UnityEngine;

namespace YARG.Settings.Types
{
    public class SliderSetting : AbstractSetting<float>
    {
        public override string AddressableName => "Setting/Slider";

        public float Min { get; private set; }
        public float Max { get; private set; }

        /// <summary>
        /// Values snap to this increment. Zero keeps the slider continuous.
        /// </summary>
        public float Step { get; private set; }

        public SliderSetting(float value, float min = float.NegativeInfinity, float max = float.PositiveInfinity,
            Action<float> onChange = null, float step = 0f) : base(onChange)
        {
            Min = min;
            Max = max;
            Step = step;

            _value = Snap(value);
        }

        protected override void SetValue(float value)
        {
            _value = Snap(value);
        }

        private float Snap(float value)
        {
            value = Mathf.Clamp(value, Min, Max);
            if (Step <= 0f)
            {
                return value;
            }

            float steps = Mathf.Round((value - Min) / Step);
            return Mathf.Clamp(Min + steps * Step, Min, Max);
        }

        public override bool ValueEquals(float value)
            => Mathf.Approximately(value, Value);
    }
}