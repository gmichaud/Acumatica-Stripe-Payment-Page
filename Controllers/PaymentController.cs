using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RestSharp;
using System.Net;
using Stripe;
using Stripe.Checkout;
using VelixoPayment.Models;

namespace VelixoPayment.Controllers
{
    public class PaymentController : Controller
    {
        private readonly ILogger<PaymentController> _logger;

        public PaymentController(ILogger<PaymentController> logger)
        {
            _logger = logger;
        }

        public IActionResult Index(string customerID, string invoiceNumber)
        {
            return View(new PaymentViewModel { CustomerID = customerID, InvoiceNumber = invoiceNumber});
        }

        public IActionResult Confirmation(string session_id, string invoiceNumber)
        {
            //TODO: We should be checking with Stripe that this is a valid session ID
            //TODO: Apply payment to Acumatica
            return View(new PaymentConfirmationViewModel { InvoiceNumber = invoiceNumber});
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult FindAndPayInvoice(PaymentViewModel model)
        {            
            CookieContainer cookieJar = new CookieContainer();
            var client = new RestClient("https://mysite.acumatica.com/");
            client.CookieContainer = cookieJar;

            try
            {
                //Login
                var loginRequest = new RestRequest("/entity/auth/login", Method.POST);
                loginRequest.AddHeader("Content-type", "application/json");
                loginRequest.AddJsonBody(new {
                    Name = "username",
                    Password = "password",
                    Tenant = "Company"
                });
                var loginResponse = client.Execute(loginRequest);
                if(!loginResponse.IsSuccessful) throw new Exception("Login failed.");

                //Get invoice
                var getInvoiceRequest = new RestRequest("/entity/Velixo/18.200.001/Invoice/Invoice/{invoiceNumber}?$select=Balance,Customer,Currency,Description");
                getInvoiceRequest.AddUrlSegment("invoiceNumber", model.InvoiceNumber.Trim().PadLeft(6,'0'));
                var getInvoiceResponse = client.Execute(getInvoiceRequest);
                if(!getInvoiceResponse.IsSuccessful) throw new Exception("Failed to locate invoice.");
                var invoice = JsonSerializer.Deserialize<Invoice>(getInvoiceResponse.Content);
                
                if(!invoice.Customer.value.Trim().Equals(model.CustomerID.Trim(), StringComparison.CurrentCultureIgnoreCase)) throw new Exception("Invoice number and/or customer ID is invalid.");
                if(invoice.Balance.value <= 0) throw new Exception("Invoice has been paid already.");

                StripeConfiguration.ApiKey = "<PUT YOUR STRIPE API KEY HERE>"; 
                var options = new SessionCreateOptions {
                    PaymentIntentData = new SessionPaymentIntentDataOptions {
                        SetupFutureUsage = "off_session",
                    },
                    PaymentMethodTypes = new List<string> {
                        "card",
                    },
                    LineItems = new List<SessionLineItemOptions> {
                        new SessionLineItemOptions {
                            Name = "Velixo Invoice #" + model.InvoiceNumber.Trim().PadLeft(6,'0'),
                            Description = invoice.Description.value,
                            Amount = (long) (invoice.Balance.value * 100),
                            Currency = invoice.Currency.value.ToLower(),
                            Quantity = 1,
                        },
                    },
                    SuccessUrl = "https://pay.velixo.com/payment/confirmation?session_id={CHECKOUT_SESSION_ID}&invoiceNumber=" + System.Net.WebUtility.UrlEncode(model.InvoiceNumber.PadLeft(6,'0')),
                    CancelUrl = "https://pay.velixo.com/"
                };

                var service = new SessionService();
                Session session = service.Create(options);

                return Json(new { Status = "success", Session = session.Id });
            }
            catch(Exception ex)
            {
                return Json(new { Status= "error", Message = ex.Message});
            }
            finally
            {
                try
                {
                    //Logout
                    client.Execute(new RestRequest("/entity/auth/logout", Method.POST));
                }
                catch(Exception ex)
                {
                    _logger.LogError(ex, "Error while trying to logout");
                }
            }
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}

class Invoice
{
    public StringValue Customer { get; set; }
    public DecimalValue Balance { get; set; }
    public StringValue Currency { get; set; }
    public StringValue Description { get; set; }
}

class DecimalValue
{
    public decimal value { get; set; }
}

class StringValue
{
    public string value { get; set; }
}