namespace VelixoPayment.Acumatica
{
    public class ValueContainer<T>
    {
        public ValueContainer(T value)
        {
            Value = value;
        }

        public T Value { get; set; }
    }
}
