namespace VelixoPayment.Acumatica
{
    public class Invoice
    {
        public ValueContainer<string> Customer { get; set; }
        public ValueContainer<decimal> Balance { get; set; }
        public ValueContainer<string> Currency { get; set; }
        public ValueContainer<string> Description { get; set; }
    }
}
