using Stripe;
using System.Collections.Generic;
using System;

namespace VelixoPayment.Acumatica
{
    public class Payment
    {
        public Guid? Id { get; set; }
        public int RowNumber { get; set; }
        public ValueContainer<string> Type { get; set; }
        public ValueContainer<string> ReferenceNbr { get; set; }
        public ValueContainer<string> CustomerID { get; set; }
        public ValueContainer<string> PaymentMethod { get; set; }
        public ValueContainer<string> CashAccount { get; set; }
        public ValueContainer<string> PaymentRef { get; set; }
        public ValueContainer<string> Description { get; set; }
        public ValueContainer<decimal> PaymentAmount { get; set; }
        public DocumentApplication?[] DocumentsToApply { get; set; }
        public Charge?[] Charges { get; set; }

    }

    public class DocumentApplication
    {
        public Guid? Id { get; set; }
        public int RowNumber { get; set; }
        public ValueContainer<string> DocType { get; set; }
        public ValueContainer<string> ReferenceNbr { get; set; }
        public ValueContainer<decimal> AmountPaid { get; set; }
        public ValueContainer<decimal> CrossRate { get; set; }
    }

    public class Charge
    {
        public ValueContainer<string> EntryTypeID { get; set; }
        public ValueContainer<decimal> Amount { get; set; }
        public ValueContainer<string> Description { get; set; }
        public ValueContainer<string> AccountID { get; set; }
        public ValueContainer<string> SubID { get; set; }
    }
}
