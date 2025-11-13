using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
            var sessionService = new SessionService();
            Session session = sessionService.Get(session_id);

            if (session.PaymentStatus == "paid")
            {
                if(!session.Metadata.ContainsKey("PaymentReferenceNbr"))
                {
                    //Fire and forget -- we don't make user wait, and if this fails we'll create payment manually.
                    Task.Factory.StartNew(async () => await CreateAndReleasePayment(session_id));
                }

                return View(new PaymentConfirmationViewModel { InvoiceNumber = session.Metadata["InvoiceNumber"] });
            }
            else
            {
                throw new Exception("Invalid payment status.");
            }
        }

        private async Task CreateAndReleasePayment(string session_id)
        {
            RestClient client = null;
            Session session = null;

            var sessionService = new SessionService();

            int retryCount = 0;
            while(true)
            {
                //Give time to Stripe to finalize payment; balance transaction will be null otherwise
                await Task.Delay(5000 * retryCount);

                session = sessionService.Get(session_id, new SessionGetOptions() { Expand = new List<string>() { "payment_intent.latest_charge.balance_transaction" } });
                if(session.PaymentIntent == null || session.PaymentIntent.LatestCharge == null || session.PaymentIntent.LatestCharge.BalanceTransaction == null)
                {
                    retryCount++;

                    if (retryCount >= 10)
                    {
                        _logger.LogWarning("Stripe payment charge data incomplete; retry count exceeded for session " + session_id);
                        return;
                    }
                }
                else
                {
                    break;
                }
            }

            try
            {
                client = GetAcumaticaRestSession();

                //Create payment
                var createPaymentRequest = new RestRequest("/entity/VelixoPayment/24.200.001/Payment?$expand=DocumentsToApply", Method.Put);
                createPaymentRequest.AddHeader("Content-Type", "application/json");
                createPaymentRequest.AddHeader("Accept", "application/json");

                createPaymentRequest.AddJsonBody(new Acumatica.Payment
                {
                    Type = new("Payment"),
                    CustomerID = new(session.Metadata["CustomerID"]),
                    PaymentMethod = new("STRIPE"),
                    CashAccount = new(session.PaymentIntent.LatestCharge.BalanceTransaction.Currency == "usd" ? "1008" : "1013"), //1008 for USD, 1013 for CAD
                    PaymentRef = new(session.PaymentIntentId),
                    PaymentAmount = new(((decimal)session.PaymentIntent.LatestCharge.BalanceTransaction.Amount) / 100),
                    DocumentsToApply = new[]
                    {
                        new Acumatica.DocumentApplication()
                        {
                            DocType = new ("Invoice"),
                            ReferenceNbr = new(session.Metadata["InvoiceNumber"]),
                            AmountPaid = new (((decimal) session.PaymentIntent.LatestCharge.BalanceTransaction.Amount) / 100),
                            CrossRate = new(session.PaymentIntent.LatestCharge.BalanceTransaction.ExchangeRate.GetValueOrDefault(1)),
                        }
                    },
                    Charges = new[]
                    {
                        new Acumatica.Charge()
                        {
                            EntryTypeID = new("CCFEES"),
                            Amount = new (((decimal) session.PaymentIntent.LatestCharge.BalanceTransaction.Fee) / 100),
                        }
                    }
                });

                var response = await client.ExecuteAsync(createPaymentRequest);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var payment = JsonConvert.DeserializeObject<Acumatica.Payment>(response.Content);

                    if(session.PaymentIntent.LatestCharge.BalanceTransaction.ExchangeRate.HasValue)
                    { 
                        //Correct payment amount (Acumatica always recalculates it when modifying the cross-rate)
                        var adjustPaymentAmountRequest = new RestRequest("/entity/VelixoPayment/24.200.001/Payment", Method.Put);
                        adjustPaymentAmountRequest.AddHeader("Content-Type", "application/json");
                        adjustPaymentAmountRequest.AddHeader("Accept", "application/json");

                        payment.DocumentsToApply[0].AmountPaid = new(((decimal)session.PaymentIntent.LatestCharge.BalanceTransaction.Amount) / 100);
                        adjustPaymentAmountRequest.AddJsonBody(payment);
                        response = await client.ExecuteAsync(adjustPaymentAmountRequest);
                    }

                    //Update Stripe metadata with payment number
                    var service = new SessionService();
                    session.Metadata["PaymentReferenceNbr"] = payment.ReferenceNbr.Value;
                    await service.UpdateAsync(session.Id, new SessionUpdateOptions() { Metadata = session.Metadata });

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

                    response = await client.ExecuteAsync(releasePaymentRequest);
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
                    await client.ExecuteAsync(new RestRequest("/entity/auth/logout", Method.Post));
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

            //Sanitize user input
            model.InvoiceNumber = model.InvoiceNumber.Trim().PadLeft(6, '0'); //TODO: Adjust to your invoice numbering sequence length
            model.CustomerID = model.CustomerID.Trim();

            try
            {
                client = GetAcumaticaRestSession();

                //Get invoice
                var getInvoiceRequest = new RestRequest("/entity/VelixoPayment/24.200.001/Invoice/Invoice/{invoiceNumber}?$select=Balance,Customer,Currency,Description");
                getInvoiceRequest.AddUrlSegment("invoiceNumber", model.InvoiceNumber);
                var getInvoiceResponse = client.Execute(getInvoiceRequest);

                var invoice = JsonConvert.DeserializeObject<Acumatica.Invoice>(getInvoiceResponse.Content);

                if (invoice.Customer == null || !invoice.Customer.Value.Trim().Equals(model.CustomerID, StringComparison.CurrentCultureIgnoreCase)) throw new Exception("Invoice number and/or customer ID is invalid.");
                if (invoice.Balance.Value <= 0) throw new Exception("Invoice has been paid already.");

                var options = new SessionCreateOptions
                {
                    PaymentIntentData = new SessionPaymentIntentDataOptions
                    {
                        SetupFutureUsage = "off_session",
                        Description = "Velixo Invoice #" + model.InvoiceNumber
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
                                    Name = "Velixo Invoice #" + model.InvoiceNumber,
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
