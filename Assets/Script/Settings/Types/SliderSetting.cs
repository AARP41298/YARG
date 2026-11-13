using System;
using UnityEngine;

namespace YARG.Settings.Types
{
    public class SliderSetting : AbstractSetting<float>
    {
        public override string AddressableName => "Setting/Slider";

        public float Min { get; private set; }
        public float Max { get; private set; }
        public bool WholeNumbers { get; }
        public bool ClampMax { get; }
        public Func<float> InputMax { get; }

        /// <summary>
        /// Values snap to this increment. Zero keeps the slider continuous.
        /// </summary>
        public float Step { get; private set; }

        public SliderSetting(float value, float min = float.NegativeInfinity, float max = float.PositiveInfinity,
            Action<float> onChange = null, bool wholeNumbers = false, bool clampMax = true,
            Func<float> inputMax = null, float step = 0f) : base(onChange)
        {
            Min = min;
            Max = max;
            WholeNumbers = wholeNumbers;
            ClampMax = clampMax;
            InputMax = inputMax;
            Step = step;

            _value = Snap(value);
        }

        protected override void SetValue(float value)
        {
            _value = Snap(value);
        }

        private float Snap(float value)
        {
            if (WholeNumbers)
                value = Mathf.Round(value);

            value = ClampMax ? Mathf.Clamp(value, Min, Max) : Mathf.Max(value, Min);
            if (Step <= 0f)
            {
                return value;
            }

            float steps = Mathf.Round((value - Min) / Step);
            value = Min + steps * Step;
            return ClampMax ? Mathf.Clamp(value, Min, Max) : Mathf.Max(value, Min);
        }

        public override bool ValueEquals(float value)
            => Mathf.Approximately(value, Value);
    }
}
