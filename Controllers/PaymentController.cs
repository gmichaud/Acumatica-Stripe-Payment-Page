using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using Stripe;
using Stripe.Checkout;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using VelixoPayment.Models;

namespace VelixoPayment.Controllers
{
    public class PaymentController : Controller
    {
        private readonly ILogger<PaymentController> _logger;
        private readonly IConfiguration _config;

        public PaymentController(ILogger<PaymentController> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;

            StripeConfiguration.ApiKey = _config.GetSection("StripeApiKey").Value;
        }

        public IActionResult Index(string customerID, string invoiceNumber)
        {
            return View(new PaymentViewModel { CustomerID = customerID, InvoiceNumber = invoiceNumber });
        }

        public IActionResult Confirmation(string session_id)
        {
            var service = new SessionService();
            Session session = service.Get(session_id);

            if (session.PaymentStatus == "paid")
            {
                if(!session.Metadata.ContainsKey("PaymentReferenceNbr"))
                {
                    //Fire and forget -- we don't make user wait, and if this fails we'll create payment manually.
                    Task.Run(() => CreateAndReleasePayment(session));
                }

                return View(new PaymentConfirmationViewModel { InvoiceNumber = session.Metadata["InvoiceNumber"] });
            }
            else
            {
                throw new Exception("Invalid payment status.");
            }
        }

        private void CreateAndReleasePayment(Session session)
        {
            RestClient client = null;

            try
            {
                client = GetAcumaticaRestSession();

                //Create payment
                var createPaymentRequest = new RestRequest("/entity/Default/24.200.001/Payment", Method.Put);
                createPaymentRequest.AddHeader("Content-Type", "application/json");
                createPaymentRequest.AddHeader("Accept", "application/json");

                createPaymentRequest.AddJsonBody(new Acumatica.Payment
                {
                    Type = new("Payment"),
                    CustomerID = new(session.Metadata["CustomerID"]),
                    PaymentMethod = new("STRIPE"),
                    CashAccount = new(session.Currency == "usd" ? "1008" : "1013"), //1008 for USD, 1013 for CAD
                    PaymentRef = new(session.PaymentIntentId),
                    PaymentAmount = new(((decimal)session.AmountTotal) / 100),
                    DocumentsToApply = new[]
                    {
                        new Acumatica.DocumentApplication()
                        {
                            DocType = new ("Invoice"),
                            ReferenceNbr = new(session.Metadata["InvoiceNumber"]),
                            AmountPaid = new (((decimal) session.AmountTotal) / 100)
                        }
                    }
                });

                var response = client.Execute(createPaymentRequest);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var payment = JsonConvert.DeserializeObject<Acumatica.Payment>(response.Content);

                    //Update Stripe metadata with payment number
                    var service = new SessionService();
                    session.Metadata["PaymentReferenceNbr"] = payment.ReferenceNbr.Value;
                    service.Update(session.Id, new SessionUpdateOptions() { Metadata = session.Metadata });

                    //Release payment
                    var releasePaymentRequest = new RestRequest("/entity/Default/24.200.001/Payment/Release", Method.Post);
                    releasePaymentRequest.AddHeader("Content-Type", "application/json");
                    releasePaymentRequest.AddHeader("Accept", "application/json");

                    releasePaymentRequest.AddJsonBody(new
                    {
                        Entity = new
                        {
                            Type = new { value = "Payment" },
                            ReferenceNbr = new { value = payment.ReferenceNbr.Value }
                        }
                    });

                    response = client.Execute(releasePaymentRequest);
                }
                else
                {
                    _logger.LogInformation(response.Content);
                    response.ThrowIfError();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while creating payment");
            }
            finally
            {
                try
                {
                    //Logout
                    client.Execute(new RestRequest("/entity/auth/logout", Method.Post));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while trying to logout");
                }
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult FindInvoiceAndCreateCheckoutSession(PaymentViewModel model)
        {
            RestClient client = null;

            try
            {
                client = GetAcumaticaRestSession();

                //Get invoice
                var getInvoiceRequest = new RestRequest("/entity/VelixoPayment/24.200.001/Invoice/Invoice/{invoiceNumber}?$select=Balance,Customer,Currency,Description");
                getInvoiceRequest.AddUrlSegment("invoiceNumber", model.InvoiceNumber.Trim().PadLeft(6, '0'));
                var getInvoiceResponse = client.Execute(getInvoiceRequest);

                var invoice = JsonConvert.DeserializeObject<Acumatica.Invoice>(getInvoiceResponse.Content);

                if (invoice.Customer == null || !invoice.Customer.Value.Trim().Equals(model.CustomerID.Trim(), StringComparison.CurrentCultureIgnoreCase)) throw new Exception("Invoice number and/or customer ID is invalid.");
                if (invoice.Balance.Value <= 0) throw new Exception("Invoice has been paid already.");

                var options = new SessionCreateOptions
                {
                    PaymentIntentData = new SessionPaymentIntentDataOptions
                    {
                        SetupFutureUsage = "off_session",
                        Description = "Velixo Invoice #" + model.InvoiceNumber.Trim().PadLeft(6, '0')
                    },
                    PaymentMethodTypes = new List<string> {
                        "card",
                    },
                    LineItems = new List<SessionLineItemOptions> {
                        new SessionLineItemOptions {
                            PriceData = new SessionLineItemPriceDataOptions
                            {
                                ProductData = new SessionLineItemPriceDataProductDataOptions()
                                {
                                    Name = "Velixo Invoice #" + model.InvoiceNumber.Trim().PadLeft(6,'0'),
                                    Description = invoice.Description.Value,
                                },
                                UnitAmount = (long) (invoice.Balance.Value * 100),
                                Currency = invoice.Currency.Value.ToLower()
                            },
                            Quantity = 1

                        },
                    },
                    Metadata = new Dictionary<string, string>() {
                        { "CustomerID", model.CustomerID },
                        { "InvoiceNumber", model.InvoiceNumber }},
                    Mode = "payment",
                    SuccessUrl = "https://pay.velixo.com/payment/confirmation?session_id={CHECKOUT_SESSION_ID}",
                    CancelUrl = "https://pay.velixo.com/"
                };
                

                var service = new SessionService();
                Session session = service.Create(options);

                return Json(new { Status = "success", Session = session.Id });
            }
            catch (Exception ex)
            {
                return Json(new { Status = "error", Message = ex.Message });
            }
            finally
            {
                try
                {
                    //Logout
                    client.Execute(new RestRequest("/entity/auth/logout", Method.Post));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while trying to logout");
                }
            }
        }

        private RestClient GetAcumaticaRestSession()
        {
            CookieContainer cookieJar = new CookieContainer();
            var client = new RestClient(new RestClientOptions()
            {
                BaseUrl = new Uri("https://erp.velixo.com/"),
                CookieContainer = cookieJar,
                ConfigureMessageHandler = (messageHandler) =>
                {
                    ((System.Net.Http.HttpClientHandler)messageHandler).UseCookies = true;
                    ((System.Net.Http.HttpClientHandler)messageHandler).CookieContainer = cookieJar;
                    return messageHandler;
                }
            });

            //Login
            var loginRequest = new RestRequest("/entity/auth/login", Method.Post);
            loginRequest.AddHeader("Content-type", "application/json");
            loginRequest.AddJsonBody(new
            {
                Name = _config.GetSection("AcumaticaUsername").Value,
                Password = _config.GetSection("AcumaticaPassword").Value,
                Tenant = _config.GetSection("AcumaticaTenant").Value
            });
            var loginResponse = client.Execute(loginRequest);
            if (!loginResponse.IsSuccessful) throw new Exception("Login failed.");

            return client;
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}