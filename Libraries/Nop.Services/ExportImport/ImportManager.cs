using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.WebPages;
using Nop.Core;
using Nop.Core.Data;
using Nop.Core.Domain.Catalog;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Media;
using Nop.Core.Domain.Messages;
using Nop.Services.Catalog;
using Nop.Services.Configuration;
using Nop.Services.Directory;
using Nop.Services.ExportImport.Help;
using Nop.Services.Media;
using Nop.Services.Messages;
using Nop.Services.Security;
using Nop.Services.Seo;
using OfficeOpenXml;

namespace Nop.Services.ExportImport
{
    /// <summary>
    /// Import manager
    /// </summary>
    public partial class ImportManager : IImportManager
    {
        #region Fields

        private readonly IProductService _productService;
        private readonly ICategoryService _categoryService;
        private readonly IManufacturerService _manufacturerService;
        private readonly IPictureService _pictureService;
        private readonly IUrlRecordService _urlRecordService;
        private readonly IStoreContext _storeContext;
        private readonly INewsLetterSubscriptionService _newsLetterSubscriptionService;
        private readonly ICountryService _countryService;
        private readonly IStateProvinceService _stateProvinceService;
        private readonly IEncryptionService _encryptionService;
        private readonly IDataProvider _dataProvider;
        private readonly MediaSettings _mediaSettings;

        #endregion

        #region Ctor

        public ImportManager(IProductService productService,
            ICategoryService categoryService,
            IManufacturerService manufacturerService,
            IPictureService pictureService,
            IUrlRecordService urlRecordService,
            IStoreContext storeContext,
            INewsLetterSubscriptionService newsLetterSubscriptionService,
            ICountryService countryService,
            IStateProvinceService stateProvinceService,
            IEncryptionService encryptionService,
            IDataProvider dataProvider,
            MediaSettings mediaSettings)
        {
            this._productService = productService;
            this._categoryService = categoryService;
            this._manufacturerService = manufacturerService;
            this._pictureService = pictureService;
            this._urlRecordService = urlRecordService;
            this._storeContext = storeContext;
            this._newsLetterSubscriptionService = newsLetterSubscriptionService;
            this._countryService = countryService;
            this._stateProvinceService = stateProvinceService;
            this._encryptionService = encryptionService;
            this._dataProvider = dataProvider;
            this._mediaSettings = mediaSettings;
        }

        #endregion

        #region Utilities

        protected virtual int GetColumnIndex(string[] properties, string columnName)
        {
            if (properties == null)
                throw new ArgumentNullException("properties");

            if (columnName == null)
                throw new ArgumentNullException("columnName");

            for (int i = 0; i < properties.Length; i++)
                if (properties[i].Equals(columnName, StringComparison.InvariantCultureIgnoreCase))
                    return i + 1; //excel indexes start from 1
            return 0;
        }

        protected virtual string ConvertColumnToString(object columnValue)
        {
            if (columnValue == null)
                return null;

            return Convert.ToString(columnValue);
        }

        protected virtual string GetMimeTypeFromFilePath(string filePath)
        {
            var mimeType = MimeMapping.GetMimeMapping(filePath);

            //little hack here because MimeMapping does not contain all mappings (e.g. PNG)
            if (mimeType == MimeTypes.ApplicationOctetStream)
                mimeType = MimeTypes.ImageJpeg;

            return mimeType;
        }

        /// <summary>
        /// Creates or loads the image
        /// </summary>
        /// <param name="picturePath">The path to the image file</param>
        /// <param name="name">The name of the object</param>
        /// <param name="picId">Image identifier, may be null</param>
        /// <returns>The image or null if the image has not changed</returns>
        protected virtual Picture LoadPicture(string picturePath, string name, int? picId = null)
        {
            if (String.IsNullOrEmpty(picturePath) || !File.Exists(picturePath))
                return null;

            var mimeType = GetMimeTypeFromFilePath(picturePath);
            var newPictureBinary = File.ReadAllBytes(picturePath);
            var pictureAlreadyExists = false;
            if (picId != null)
            {
                //compare with existing product pictures
                var existingPicture = _pictureService.GetPictureById(picId.Value);

                var existingBinary = _pictureService.LoadPictureBinary(existingPicture);
                //picture binary after validation (like in database)
                var validatedPictureBinary = _pictureService.ValidatePicture(newPictureBinary, mimeType);
                if (existingBinary.SequenceEqual(validatedPictureBinary) ||
                    existingBinary.SequenceEqual(newPictureBinary))
                {
                    pictureAlreadyExists = true;
                }
            }

            if (pictureAlreadyExists) return null;

            var newPicture = _pictureService.InsertPicture(newPictureBinary, mimeType,
                _pictureService.GetPictureSeName(name));
            return newPicture;
        }

        protected virtual void ImportProductImagesUsingServices(IList<ProductPictureMetadata> productPictureMetadata)
        {
            foreach (var product in productPictureMetadata)
            {
                foreach (var picturePath in new[] { product.Picture1Path, product.Picture2Path, product.Picture3Path })
                {
                    if (String.IsNullOrEmpty(picturePath))
                        continue;

                    var mimeType = GetMimeTypeFromFilePath(picturePath);
                    var newPictureBinary = File.ReadAllBytes(picturePath);
                    var pictureAlreadyExists = false;
                    if (!product.IsNew)
                    {
                        //compare with existing product pictures
                        var existingPictures = _pictureService.GetPicturesByProductId(product.ProductItem.Id);
                        foreach (var existingPicture in existingPictures)
                        {
                            var existingBinary = _pictureService.LoadPictureBinary(existingPicture);
                            //picture binary after validation (like in database)
                            var validatedPictureBinary = _pictureService.ValidatePicture(newPictureBinary, mimeType);
                            if (!existingBinary.SequenceEqual(validatedPictureBinary) &&
                                !existingBinary.SequenceEqual(newPictureBinary))
                                continue;
                            //the same picture content
                            pictureAlreadyExists = true;
                            break;
                        }
                    }

                    if (pictureAlreadyExists)
                        continue;
                    var newPicture = _pictureService.InsertPicture(newPictureBinary, mimeType, _pictureService.GetPictureSeName(product.ProductItem.Name));
                    product.ProductItem.ProductPictures.Add(new ProductPicture
                    {
                        //EF has some weird issue if we set "Picture = newPicture" instead of "PictureId = newPicture.Id"
                        //pictures are duplicated
                        //maybe because entity size is too large
                        PictureId = newPicture.Id,
                        DisplayOrder = 1,
                    });
                    _productService.UpdateProduct(product.ProductItem);
                }
            }
        }

        protected virtual void ImportProductImagesUsingHash(IList<ProductPictureMetadata> productPictureMetadata, IList<Product> allProductsBySku)
        {
            //performance optimization, load all pictures hashes
            //it will only be used if the images are stored in the SQL Server database (not compact)
            var takeCount = _dataProvider.SupportedLengthOfBinaryHash() - 1;
            var productsImagesIds = _productService.GetProductsImagesIds(allProductsBySku.Select(p => p.Id).ToArray());
            var allPicturesHashes = _pictureService.GetPicturesHash(productsImagesIds.SelectMany(p => p.Value).ToArray());

            foreach (var product in productPictureMetadata)
            {
                foreach (var picturePath in new[] { product.Picture1Path, product.Picture2Path, product.Picture3Path })
                {
                    if (String.IsNullOrEmpty(picturePath))
                        continue;

                    var mimeType = GetMimeTypeFromFilePath(picturePath);
                    var newPictureBinary = File.ReadAllBytes(picturePath);
                    var pictureAlreadyExists = false;
                    if (!product.IsNew)
                    {
                        var newImageHash = _encryptionService.CreateHash(newPictureBinary.Take(takeCount).ToArray());
                        var newValidatedImageHash = _encryptionService.CreateHash(_pictureService.ValidatePicture(newPictureBinary, mimeType).Take(takeCount).ToArray());

                        var imagesIds = productsImagesIds.ContainsKey(product.ProductItem.Id)
                            ? productsImagesIds[product.ProductItem.Id]
                            : new int[0];

                        pictureAlreadyExists = allPicturesHashes.Where(p => imagesIds.Contains(p.Key)).Select(p => p.Value).Any(p => p == newImageHash || p == newValidatedImageHash);
                    }

                    if (pictureAlreadyExists)
                        continue;
                    var newPicture = _pictureService.InsertPicture(newPictureBinary, mimeType, _pictureService.GetPictureSeName(product.ProductItem.Name));
                    product.ProductItem.ProductPictures.Add(new ProductPicture
                    {
                        //EF has some weird issue if we set "Picture = newPicture" instead of "PictureId = newPicture.Id"
                        //pictures are duplicated
                        //maybe because entity size is too large
                        PictureId = newPicture.Id,
                        DisplayOrder = 1,
                    });
                    _productService.UpdateProduct(product.ProductItem);
                }
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Import products from XLSX file
        /// </summary>
        /// <param name="stream">Stream</param>
        public virtual void ImportProductsFromXlsx(Stream stream)
        {
            //var start = DateTime.Now;
            using (var xlPackage = new ExcelPackage(stream))
            {
                // get the first worksheet in the workbook
                var worksheet = xlPackage.Workbook.Worksheets.FirstOrDefault();
                if (worksheet == null)
                    throw new NopException("No worksheet found");

                //the columns
                var properties = new List<PropertyByName<Product>>();
                var poz = 1;
                while (true)
                {
                    try
                    {
                        var cell = worksheet.Cells[1, poz];

                        if (cell == null || cell.Value == null || String.IsNullOrEmpty(cell.Value.ToString()))
                            break;

                        poz += 1;
                        properties.Add(new PropertyByName<Product>(cell.Value.ToString()));
                    }
                    catch
                    {
                       break;
                    }
                }

                var manager = new PropertyManager<Product>(properties.ToArray());

                var endRow = 2;
                var allCategoriesIds = new List<int>();
                var allSku = new List<string>();

                var tempProperty = manager.GetProperty("CategoryIds");
                var categoryCellNum = tempProperty.Return(p=> p.PropertyOrderPosition, -1);

                tempProperty = manager.GetProperty("SKU");
                var skuCellNum = tempProperty.Return(p => p.PropertyOrderPosition, -1);

                var allManufacturersIds = new List<int>();
                tempProperty = manager.GetProperty("ManufacturerIds");
                var manufacturerCellNum = tempProperty.Return(p => p.PropertyOrderPosition, -1);

                //find end of data
                while (true)
                {
                    var allColumnsAreEmpty = manager.GetProperties
                        .Select(property => worksheet.Cells[endRow, property.PropertyOrderPosition])
                        .All(cell => cell == null || cell.Value == null || String.IsNullOrEmpty(cell.Value.ToString()));

                    if (allColumnsAreEmpty)
                        break;

                    if(categoryCellNum > 0)
                    { 
                        var categoryIds = worksheet.Cells[endRow, categoryCellNum].Value.Return(p => p.ToString(), string.Empty);

                        if (!categoryIds.IsEmpty())
                            allCategoriesIds.AddRange(categoryIds.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToInt32(x.Trim())));
                    }

                    if (skuCellNum > 0)
                    {
                        var sku = worksheet.Cells[endRow, skuCellNum].Value.Return(p => p.ToString(), string.Empty);

                        if (!sku.IsEmpty())
                            allSku.Add(sku);
                    }

                    if(manufacturerCellNum > 0)
                    { 
                        var manufacturerIds = worksheet.Cells[endRow, manufacturerCellNum].Value.Return(p => p.ToString(), string.Empty);
                        if (!manufacturerIds.IsEmpty())
                            allManufacturersIds.AddRange(manufacturerIds.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToInt32(x.Trim())));
                    }
                    endRow++;
                }

                //performance optimization, the check for the existence of the categories in one SQL request
                var notExistingCategories = _categoryService.GetNotExistingCategories(allCategoriesIds.ToArray());
                if (notExistingCategories.Any())
                {
                    throw new ArgumentException(string.Format("The following category ID(s) don't exist - {0}", string.Join(", ", notExistingCategories)));
                }

                //performance optimization, the check for the existence of the manufacturers in one SQL request
                var notExistingManufacturers = _manufacturerService.GetNotExistingManufacturers(allManufacturersIds.ToArray());
                if (notExistingManufacturers.Any())
                {
                    throw new ArgumentException(string.Format("The following manufacturer ID(s) don't exist - {0}", string.Join(", ", notExistingManufacturers)));
                }

                //performance optimization, load all products by SKU in one SQL request
                var allProductsBySku = _productService.GetProductsBySku(allSku.ToArray());

                //performance optimization, load all categories IDs for products in one SQL request
                var allProductsCategoryIds = _categoryService.GetProductCategoryIds(allProductsBySku.Select(p => p.Id).ToArray());

                //performance optimization, load all manufacturers IDs for products in one SQL request
                var allProductsManufacturerIds = _manufacturerService.GetProductManufacturerIds(allProductsBySku.Select(p => p.Id).ToArray());

                //product to import images
                var productPictureMetadata = new List<ProductPictureMetadata>();

                for (var iRow = 2; iRow < endRow; iRow++)
                {
                    manager.ReadFromXlsx(worksheet, iRow);

                    var product = skuCellNum > 0 ? allProductsBySku.FirstOrDefault(p => p.Sku == manager.GetProperty("SKU").StringValue) : null;

                    var isNew = product == null;

                    product = product ?? new Product();

                    if (isNew)
                        product.CreatedOnUtc = DateTime.UtcNow;

                    foreach (var property in manager.GetProperties)
                    {
                        switch (property.PropertyName)
                        {
                            case "ProductTypeId":
                                product.ProductTypeId = property.IntValue;
                                break;
                            case "ParentGroupedProductId":
                                product.ParentGroupedProductId = property.IntValue;
                                break;
                            case "VisibleIndividually":
                                product.VisibleIndividually = property.BooleanValue;
                                break;
                            case "Name":
                                product.Name = property.StringValue;
                                break;
                            case "ShortDescription":
                                product.ShortDescription = property.StringValue;
                                break;
                            case "FullDescription":
                                product.FullDescription = property.StringValue;
                                break;
                            case "VendorId":
                                product.VendorId = property.IntValue;
                                break;
                            case "ProductTemplateId":
                                product.ProductTemplateId = property.IntValue;
                                break;
                            case "ShowOnHomePage":
                                product.ShowOnHomePage = property.BooleanValue;
                                break;
                            case "MetaKeywords":
                                product.MetaKeywords = property.StringValue;
                                break;
                            case "MetaDescription":
                                product.MetaDescription = property.StringValue;
                                break;
                            case "MetaTitle":
                                product.MetaTitle = property.StringValue;
                                break;
                            case "AllowCustomerReviews":
                                product.AllowCustomerReviews = property.BooleanValue;
                                break;
                            case "Published":
                                product.Published = property.BooleanValue;
                                break;
                            case "SKU":
                                product.Sku = property.StringValue;
                                break;
                            case "ManufacturerPartNumber":
                                product.ManufacturerPartNumber = property.StringValue;
                                break;
                            case "Gtin":
                                product.Gtin = property.StringValue;
                                break;
                            case "IsGiftCard":
                                product.IsGiftCard = property.BooleanValue;
                                break;
                            case "GiftCardTypeId":
                                product.GiftCardTypeId = property.IntValue;
                                break;
                            case "OverriddenGiftCardAmount":
                                product.OverriddenGiftCardAmount = property.DecimalValue;
                                break;
                            case "RequireOtherProducts":
                                product.RequireOtherProducts = property.BooleanValue;
                                break;
                            case "RequiredProductIds":
                                product.RequiredProductIds = property.StringValue;
                                break;
                            case "AutomaticallyAddRequiredProducts":
                                product.AutomaticallyAddRequiredProducts = property.BooleanValue;
                                break;
                            case "IsDownload":
                                product.IsDownload = property.BooleanValue;
                                break;
                            case "DownloadId":
                                product.DownloadId = property.IntValue;
                                break;
                            case "UnlimitedDownloads":
                                product.UnlimitedDownloads = property.BooleanValue;
                                break;
                            case "MaxNumberOfDownloads":
                                product.MaxNumberOfDownloads = property.IntValue;
                                break;
                            case "DownloadActivationTypeId":
                                product.DownloadActivationTypeId = property.IntValue;
                                break;
                            case "HasSampleDownload":
                                product.HasSampleDownload = property.BooleanValue;
                                break;
                            case "SampleDownloadId":
                                product.SampleDownloadId = property.IntValue;
                                break;
                            case "HasUserAgreement":
                                product.HasUserAgreement = property.BooleanValue;
                                break;
                            case "UserAgreementText":
                                product.UserAgreementText = property.StringValue;
                                break;
                            case "IsRecurring":
                                product.IsRecurring = property.BooleanValue;
                                break;
                            case "RecurringCycleLength":
                                product.RecurringCycleLength = property.IntValue;
                                break;
                            case "RecurringCyclePeriodId":
                                product.RecurringCyclePeriodId = property.IntValue;
                                break;
                            case "RecurringTotalCycles":
                                product.RecurringTotalCycles = property.IntValue;
                                break;
                            case "IsRental":
                                product.IsRental = property.BooleanValue;
                                break;
                            case "RentalPriceLength":
                                product.RentalPriceLength = property.IntValue;
                                break;
                            case "RentalPricePeriodId":
                                product.RentalPricePeriodId = property.IntValue;
                                break;
                            case "IsShipEnabled":
                                product.IsShipEnabled = property.BooleanValue;
                                break;
                            case "IsFreeShipping":
                                product.IsFreeShipping = property.BooleanValue;
                                break;
                            case "ShipSeparately":
                                product.ShipSeparately = property.BooleanValue;
                                break;
                            case "AdditionalShippingCharge":
                                product.AdditionalShippingCharge = property.DecimalValue;
                                break;
                            case "DeliveryDateId":
                                product.DeliveryDateId = property.IntValue;
                                break;
                            case "IsTaxExempt":
                                product.IsTaxExempt = property.BooleanValue;
                                break;
                            case "TaxCategoryId":
                                product.TaxCategoryId = property.IntValue;
                                break;
                            case "IsTelecommunicationsOrBroadcastingOrElectronicServices":
                                product.IsTelecommunicationsOrBroadcastingOrElectronicServices = property.BooleanValue;
                                break;
                            case "ManageInventoryMethodId":
                                product.ManageInventoryMethodId = property.IntValue;
                                break;
                            case "UseMultipleWarehouses":
                                product.UseMultipleWarehouses = property.BooleanValue;
                                break;
                            case "WarehouseId":
                                product.WarehouseId = property.IntValue;
                                break;
                            case "StockQuantity":
                                product.StockQuantity = property.IntValue;
                                break;
                            case "DisplayStockAvailability":
                                product.DisplayStockAvailability = property.BooleanValue;
                                break;
                            case "DisplayStockQuantity":
                                product.DisplayStockQuantity = property.BooleanValue;
                                break;
                            case "MinStockQuantity":
                                product.MinStockQuantity = property.IntValue;
                                break;
                            case "LowStockActivityId":
                                product.LowStockActivityId = property.IntValue;
                                break;
                            case "NotifyAdminForQuantityBelow":
                                product.NotifyAdminForQuantityBelow = property.IntValue;
                                break;
                            case "BackorderModeId":
                                product.BackorderModeId = property.IntValue;
                                break;
                            case "AllowBackInStockSubscriptions":
                                product.AllowBackInStockSubscriptions = property.BooleanValue;
                                break;
                            case "OrderMinimumQuantity":
                                product.OrderMinimumQuantity = property.IntValue;
                                break;
                            case "OrderMaximumQuantity":
                                product.OrderMaximumQuantity = property.IntValue;
                                break;
                            case "AllowedQuantities":
                                product.AllowedQuantities = property.StringValue;
                                break;
                            case "AllowAddingOnlyExistingAttributeCombinations":
                                product.AllowAddingOnlyExistingAttributeCombinations = property.BooleanValue;
                                break;
                            case "DisableBuyButton":
                                product.DisableBuyButton = property.BooleanValue;
                                break;
                            case "DisableWishlistButton":
                                product.DisableWishlistButton = property.BooleanValue;
                                break;
                            case "AvailableForPreOrder":
                                product.AvailableForPreOrder = property.BooleanValue;
                                break;
                            case "PreOrderAvailabilityStartDateTimeUtc":
                                product.PreOrderAvailabilityStartDateTimeUtc = property.DateTimeNullable;
                                break;
                            case "CallForPrice":
                                product.CallForPrice = property.BooleanValue;
                                break;
                            case "Price":
                                product.Price = property.DecimalValue;
                                break;
                            case "OldPrice":
                                product.OldPrice = property.DecimalValue;
                                break;
                            case "ProductCost":
                                product.ProductCost = property.DecimalValue;
                                break;
                            case "SpecialPrice":
                                product.SpecialPrice = property.DecimalValueNullable;
                                break;
                            case "SpecialPriceStartDateTimeUtc":
                                product.SpecialPriceStartDateTimeUtc = property.DateTimeNullable;
                                break;
                            case "SpecialPriceEndDateTimeUtc":
                                product.SpecialPriceEndDateTimeUtc = property.DateTimeNullable;
                                break;
                            case "CustomerEntersPrice":
                                product.CustomerEntersPrice = property.BooleanValue;
                                break;
                            case "MinimumCustomerEnteredPrice":
                                product.MinimumCustomerEnteredPrice = property.DecimalValue;
                                break;
                            case "MaximumCustomerEnteredPrice":
                                product.MaximumCustomerEnteredPrice = property.DecimalValue;
                                break;
                            case "BasepriceEnabled":
                                product.BasepriceEnabled = property.BooleanValue;
                                break;
                            case "BasepriceAmount":
                                product.BasepriceAmount = property.DecimalValue;
                                break;
                            case "BasepriceUnitId":
                                product.BasepriceUnitId = property.IntValue;
                                break;
                            case "BasepriceBaseAmount":
                                product.BasepriceBaseAmount = property.DecimalValue;
                                break;
                            case "BasepriceBaseUnitId":
                                product.BasepriceBaseUnitId = property.IntValue;
                                break;
                            case "MarkAsNew":
                                product.MarkAsNew = property.BooleanValue;
                                break;
                            case "MarkAsNewStartDateTimeUtc":
                                product.MarkAsNewStartDateTimeUtc = property.DateTimeNullable;
                                break;
                            case "MarkAsNewEndDateTimeUtc":
                                product.MarkAsNewEndDateTimeUtc = property.DateTimeNullable;
                                break;
                            case "Weight":
                                product.Weight = property.DecimalValue;
                                break;
                            case "Length":
                                product.Length = property.DecimalValue;
                                break;
                            case "Width":
                                product.Width = property.DecimalValue;
                                break;
                            case "Height":
                                product.Height = property.DecimalValue;
                                break;
                        }
                    }

                    //set default product type id
                    if (isNew && properties.All(p => p.PropertyName != "ProductTypeId"))
                        product.ProductType = ProductType.SimpleProduct;

                    product.UpdatedOnUtc = DateTime.UtcNow;

                    if (isNew)
                    {
                        _productService.InsertProduct(product);
                    }
                    else
                    {
                        _productService.UpdateProduct(product);
                    }

                    tempProperty = manager.GetProperty("SeName");
                    if (tempProperty != null)
                    {
                        var seName = tempProperty.StringValue;
                        //search engine name
                        _urlRecordService.SaveSlug(product, product.ValidateSeName(seName, product.Name, true), 0);
                    }

                    tempProperty = manager.GetProperty("CategoryIds");

                    if(tempProperty != null)
                    { 
                        var categoryIds = tempProperty.StringValue;

                        //category mappings
                        var categories = isNew || !allProductsCategoryIds.ContainsKey(product.Id) ? new int[0] : allProductsCategoryIds[product.Id];
                        foreach (var categoryId in categoryIds.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToInt32(x.Trim())))
                        {
                            if (categories.Any(c => c == categoryId))
                                continue;
                       
                            var productCategory = new ProductCategory
                            {
                                ProductId = product.Id,
                                CategoryId = categoryId,
                                IsFeaturedProduct = false,
                                DisplayOrder = 1
                            };
                            _categoryService.InsertProductCategory(productCategory);
                        }
                    }

                    tempProperty = manager.GetProperty("ManufacturerIds");
                    if (tempProperty != null)
                    {
                        var manufacturerIds = tempProperty.StringValue;

                        //manufacturer mappings
                        var manufacturers = isNew || !allProductsManufacturerIds.ContainsKey(product.Id) ? new int[0] : allProductsManufacturerIds[product.Id];
                        foreach (var manufacturerId in manufacturerIds.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToInt32(x.Trim())))
                        {
                            if (manufacturers.Any(c => c == manufacturerId))
                                continue;

                            var productManufacturer = new ProductManufacturer
                            {
                                ProductId = product.Id,
                                ManufacturerId = manufacturerId,
                                IsFeaturedProduct = false,
                                DisplayOrder = 1
                            };
                            _manufacturerService.InsertProductManufacturer(productManufacturer);
                        }
                    }
                    
                    var picture1 = manager.GetProperty("Picture1").Return(p => p.StringValue, "");
                    var picture2 = manager.GetProperty("Picture2").Return(p => p.StringValue, "");
                    var picture3 = manager.GetProperty("Picture3").Return(p => p.StringValue, "");

                    productPictureMetadata.Add(new ProductPictureMetadata
                    {
                        ProductItem = product,
                        Picture1Path = picture1,
                        Picture2Path = picture2,
                        Picture3Path = picture3,
                        IsNew = isNew
                    });

                    //update "HasTierPrices" and "HasDiscountsApplied" properties
                    //_productService.UpdateHasTierPricesProperty(product);
                    //_productService.UpdateHasDiscountsApplied(product);
                }
               
                if (_mediaSettings.ImportProductImagesUsingHash && _pictureService.StoreInDb && _dataProvider.SupportedLengthOfBinaryHash() > 0)
                    ImportProductImagesUsingHash(productPictureMetadata, allProductsBySku);
                else
                    ImportProductImagesUsingServices(productPictureMetadata);
            }
            //Trace.WriteLine(DateTime.Now-start);
        }
        
        /// <summary>
        /// Import newsletter subscribers from TXT file
        /// </summary>
        /// <param name="stream">Stream</param>
        /// <returns>Number of imported subscribers</returns>
        public virtual int ImportNewsletterSubscribersFromTxt(Stream stream)
        {
            int count = 0;
            using (var reader = new StreamReader(stream))
            {
                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    if (String.IsNullOrWhiteSpace(line))
                        continue;
                    string[] tmp = line.Split(',');

                    string email;
                    bool isActive = true;
                    int storeId = _storeContext.CurrentStore.Id;
                    //parse
                    if (tmp.Length == 1)
                    {
                        //"email" only
                        email = tmp[0].Trim();
                    }
                    else if (tmp.Length == 2)
                    {
                        //"email" and "active" fields specified
                        email = tmp[0].Trim();
                        isActive = Boolean.Parse(tmp[1].Trim());
                    }
                    else if (tmp.Length == 3)
                    {
                        //"email" and "active" and "storeId" fields specified
                        email = tmp[0].Trim();
                        isActive = Boolean.Parse(tmp[1].Trim());
                        storeId = Int32.Parse(tmp[2].Trim());
                    }
                    else
                        throw new NopException("Wrong file format");

                    //import
                    var subscription = _newsLetterSubscriptionService.GetNewsLetterSubscriptionByEmailAndStoreId(email, storeId);
                    if (subscription != null)
                    {
                        subscription.Email = email;
                        subscription.Active = isActive;
                        _newsLetterSubscriptionService.UpdateNewsLetterSubscription(subscription);
                    }
                    else
                    {
                        subscription = new NewsLetterSubscription
                        {
                            Active = isActive,
                            CreatedOnUtc = DateTime.UtcNow,
                            Email = email,
                            StoreId = storeId,
                            NewsLetterSubscriptionGuid = Guid.NewGuid()
                        };
                        _newsLetterSubscriptionService.InsertNewsLetterSubscription(subscription);
                    }
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Import states from TXT file
        /// </summary>
        /// <param name="stream">Stream</param>
        /// <returns>Number of imported states</returns>
        public virtual int ImportStatesFromTxt(Stream stream)
        {
            int count = 0;
            using (var reader = new StreamReader(stream))
            {
                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    if (String.IsNullOrWhiteSpace(line))
                        continue;
                    string[] tmp = line.Split(',');

                    if (tmp.Length != 5)
                        throw new NopException("Wrong file format");

                    //parse
                    var countryTwoLetterIsoCode = tmp[0].Trim();
                    var name = tmp[1].Trim();
                    var abbreviation = tmp[2].Trim();
                    bool published = Boolean.Parse(tmp[3].Trim());
                    int displayOrder = Int32.Parse(tmp[4].Trim());

                    var country = _countryService.GetCountryByTwoLetterIsoCode(countryTwoLetterIsoCode);
                    if (country == null)
                    {
                        //country cannot be loaded. skip
                        continue;
                    }

                    //import
                    var states = _stateProvinceService.GetStateProvincesByCountryId(country.Id, showHidden: true);
                    var state = states.FirstOrDefault(x => x.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));

                    if (state != null)
                    {
                        state.Abbreviation = abbreviation;
                        state.Published = published;
                        state.DisplayOrder = displayOrder;
                        _stateProvinceService.UpdateStateProvince(state);
                    }
                    else
                    {
                        state = new StateProvince
                        {
                            CountryId = country.Id,
                            Name = name,
                            Abbreviation = abbreviation,
                            Published = published,
                            DisplayOrder = displayOrder,
                        };
                        _stateProvinceService.InsertStateProvince(state);
                    }
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Import manufacturers from XLSX file
        /// </summary>
        /// <param name="stream">Stream</param>
        public virtual void ImportManufacturersFromXlsx(Stream stream)
        {
            //property array
            var properties = new[]
            {
                new PropertyByName<Manufacturer>("Id"),
                new PropertyByName<Manufacturer>("Name"),
                new PropertyByName<Manufacturer>("Description"),
                new PropertyByName<Manufacturer>("ManufacturerTemplateId"),
                new PropertyByName<Manufacturer>("MetaKeywords"),
                new PropertyByName<Manufacturer>("MetaDescription"),
                new PropertyByName<Manufacturer>("MetaTitle"),
                new PropertyByName<Manufacturer>("Picture"),
                new PropertyByName<Manufacturer>("PageSize"),
                new PropertyByName<Manufacturer>("AllowCustomersToSelectPageSize"),
                new PropertyByName<Manufacturer>("PageSizeOptions"),
                new PropertyByName<Manufacturer>("PriceRanges"),
                new PropertyByName<Manufacturer>("Published"),
                new PropertyByName<Manufacturer>("DisplayOrder")
            };

            var manager = new PropertyManager<Manufacturer>(properties);

            using (var xlPackage = new ExcelPackage(stream))
            {
                // get the first worksheet in the workbook
                var worksheet = xlPackage.Workbook.Worksheets.FirstOrDefault();
                if (worksheet == null)
                    throw new NopException("No worksheet found");

                var iRow = 2;

                while (true)
                {
                    var allColumnsAreEmpty = manager.GetProperties
                        .Select(property => worksheet.Cells[iRow, property.PropertyOrderPosition])
                        .All(cell => cell == null || cell.Value == null || String.IsNullOrEmpty(cell.Value.ToString()));

                    if (allColumnsAreEmpty)
                        break;

                    manager.ReadFromXlsx(worksheet, iRow);

                    var manufacturer = _manufacturerService.GetManufacturerById(manager.GetProperty("Id").IntValue);

                    var isNew = manufacturer == null;

                    manufacturer = manufacturer ?? new Manufacturer();

                    if (isNew)
                        manufacturer.CreatedOnUtc = DateTime.UtcNow;

                    manufacturer.Name = manager.GetProperty("Name").StringValue;
                    manufacturer.Description = manager.GetProperty("Description").StringValue;
                    manufacturer.ManufacturerTemplateId = manager.GetProperty("ManufacturerTemplateId").IntValue;
                    manufacturer.MetaKeywords = manager.GetProperty("MetaKeywords").StringValue;
                    manufacturer.MetaDescription = manager.GetProperty("MetaDescription").StringValue;
                    manufacturer.MetaTitle = manager.GetProperty("MetaTitle").StringValue;
                    var picture = LoadPicture(manager.GetProperty("Picture").StringValue, manufacturer.Name,
                        isNew ? null : (int?) manufacturer.PictureId);
                    manufacturer.PageSize = manager.GetProperty("PageSize").IntValue;
                    manufacturer.AllowCustomersToSelectPageSize = manager.GetProperty("AllowCustomersToSelectPageSize").BooleanValue;
                    manufacturer.PageSizeOptions = manager.GetProperty("PageSizeOptions").StringValue;
                    manufacturer.PriceRanges = manager.GetProperty("PriceRanges").StringValue;
                    manufacturer.Published = manager.GetProperty("Published").BooleanValue;
                    manufacturer.DisplayOrder = manager.GetProperty("DisplayOrder").IntValue;

                    if (picture != null)
                        manufacturer.PictureId = picture.Id;

                    manufacturer.UpdatedOnUtc = DateTime.UtcNow;

                    if (isNew)
                        _manufacturerService.InsertManufacturer(manufacturer);
                    else
                        _manufacturerService.UpdateManufacturer(manufacturer);

                    iRow++;
                }
            }
        }

        /// <summary>
        /// Import categories from XLSX file
        /// </summary>
        /// <param name="stream">Stream</param>
        public virtual void ImportCategoriesFromXlsx(Stream stream)
        {
            var properties = new[]
            {
                new PropertyByName<Category>("Id"),
                new PropertyByName<Category>("Name"),
                new PropertyByName<Category>("Description"),
                new PropertyByName<Category>("CategoryTemplateId"),
                new PropertyByName<Category>("MetaKeywords"),
                new PropertyByName<Category>("MetaDescription"),
                new PropertyByName<Category>("MetaTitle"),
                new PropertyByName<Category>("ParentCategoryId"),
                new PropertyByName<Category>("Picture"),
                new PropertyByName<Category>("PageSize"),
                new PropertyByName<Category>("AllowCustomersToSelectPageSize"),
                new PropertyByName<Category>("PageSizeOptions"),
                new PropertyByName<Category>("PriceRanges"),
                new PropertyByName<Category>("ShowOnHomePage"),
                new PropertyByName<Category>("IncludeInTopMenu"),
                new PropertyByName<Category>("Published"),
                new PropertyByName<Category>("DisplayOrder")
            };

            var manager = new PropertyManager<Category>(properties);

            using (var xlPackage = new ExcelPackage(stream))
            {
                // get the first worksheet in the workbook
                var worksheet = xlPackage.Workbook.Worksheets.FirstOrDefault();
                if (worksheet == null)
                    throw new NopException("No worksheet found");

                var iRow = 2;

                while (true)
                {
                    var allColumnsAreEmpty = manager.GetProperties
                        .Select(property => worksheet.Cells[iRow, property.PropertyOrderPosition])
                        .All(cell => cell == null || cell.Value == null || String.IsNullOrEmpty(cell.Value.ToString()));

                    if (allColumnsAreEmpty)
                        break;

                    manager.ReadFromXlsx(worksheet, iRow);

                    var category = _categoryService.GetCategoryById(manager.GetProperty("Id").IntValue);

                    var isNew = category == null;

                    category = category ?? new Category();

                    if (isNew)
                        category.CreatedOnUtc = DateTime.UtcNow;

                    category.Name = manager.GetProperty("Name").StringValue;
                    category.Description = manager.GetProperty("Description").StringValue;

                    category.CategoryTemplateId = manager.GetProperty("CategoryTemplateId").IntValue;
                    category.MetaKeywords = manager.GetProperty("MetaKeywords").StringValue;
                    category.MetaDescription = manager.GetProperty("MetaDescription").StringValue;
                    category.MetaTitle = manager.GetProperty("MetaTitle").StringValue;
                    category.ParentCategoryId = manager.GetProperty("ParentCategoryId").IntValue;
                    var picture = LoadPicture(manager.GetProperty("Picture").StringValue, category.Name, isNew ? null : (int?) category.PictureId);
                    category.PageSize = manager.GetProperty("PageSize").IntValue;
                    category.AllowCustomersToSelectPageSize = manager.GetProperty("AllowCustomersToSelectPageSize").BooleanValue;
                    category.PageSizeOptions = manager.GetProperty("PageSizeOptions").StringValue;
                    category.PriceRanges = manager.GetProperty("PriceRanges").StringValue;
                    category.ShowOnHomePage = manager.GetProperty("ShowOnHomePage").BooleanValue;
                    category.IncludeInTopMenu = manager.GetProperty("IncludeInTopMenu").BooleanValue;
                    category.Published = manager.GetProperty("Published").BooleanValue;
                    category.DisplayOrder = manager.GetProperty("DisplayOrder").IntValue;

                    if (picture != null)
                        category.PictureId = picture.Id;

                    category.UpdatedOnUtc = DateTime.UtcNow;

                    if (isNew)
                        _categoryService.InsertCategory(category);
                    else
                        _categoryService.UpdateCategory(category);

                    iRow++;
                }
            }
        }

        #endregion

        #region Nested classes

        protected class ProductPictureMetadata
        {
            public Product ProductItem { get; set; }
            public string Picture1Path { get; set; }
            public string Picture2Path { get; set; }
            public string Picture3Path { get; set; }
            public bool IsNew { get; set; }
        }

        #endregion
    }
}
