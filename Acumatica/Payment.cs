using Stripe;
using System.Collections.Generic;
using System;

namespace VelixoPayment.Acumatica
{
    public class Payment
    {
        public ValueContainer<string> Type { get; set; }
        public ValueContainer<string> ReferenceNbr { get; set; }
        public ValueContainer<string> CustomerID { get; set; }
        public ValueContainer<string> PaymentMethod { get; set; }
        public ValueContainer<string> CashAccount { get; set; }
        public ValueContainer<string> PaymentRef { get; set; }
        public ValueContainer<string> Description { get; set; }
        public ValueContainer<decimal> PaymentAmount { get; set; }
        public DocumentApplication[] DocumentsToApply { get; set; }

    }

    public class DocumentApplication
    {
        public ValueContainer<string> DocType { get; set; }
        public ValueContainer<string> ReferenceNbr { get; set; }
        public ValueContainer<decimal> AmountPaid { get; set; }
    }
}
