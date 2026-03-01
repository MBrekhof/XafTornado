using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EF;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.SystemModule;
using DevExpress.ExpressApp.Updating;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using XafTornado.Module.BusinessObjects;

namespace XafTornado.Module.DatabaseUpdate
{
    // For more typical usage scenarios, be sure to check out https://docs.devexpress.com/eXpressAppFramework/DevExpress.ExpressApp.Updating.ModuleUpdater
    public class Updater : ModuleUpdater
    {
        public Updater(IObjectSpace objectSpace, Version currentDBVersion) :
            base(objectSpace, currentDBVersion)
        {
        }
        public override void UpdateDatabaseAfterUpdateSchema()
        {
            base.UpdateDatabaseAfterUpdateSchema();
            //string name = "MyName";
            //EntityObject1 theObject = ObjectSpace.FirstOrDefault<EntityObject1>(u => u.Name == name);
            //if(theObject == null) {
            //    theObject = ObjectSpace.CreateObject<EntityObject1>();
            //    theObject.Name = name;
            //}

            // The code below creates users and roles for testing purposes only.
            // In production code, you can create users and assign roles to them automatically, as described in the following help topic:
            // https://docs.devexpress.com/eXpressAppFramework/119064/data-security-and-safety/security-system/authentication
#if !RELEASE
            // If a role doesn't exist in the database, create this role
            var defaultRole = CreateDefaultRole();
            var adminRole = CreateAdminRole();

            ObjectSpace.CommitChanges(); //This line persists created object(s).

            UserManager userManager = ObjectSpace.ServiceProvider.GetRequiredService<UserManager>();

            // If a user named 'User' doesn't exist in the database, create this user
            if (userManager.FindUserByName<ApplicationUser>(ObjectSpace, "User") == null)
            {
                // Set a password if the standard authentication type is used
                string EmptyPassword = "";
                _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, "User", EmptyPassword, (user) =>
                {
                    // Add the Users role to the user
                    user.Roles.Add(defaultRole);
                });
            }

            // If a user named 'Admin' doesn't exist in the database, create this user
            if (userManager.FindUserByName<ApplicationUser>(ObjectSpace, "Admin") == null)
            {
                // Set a password if the standard authentication type is used
                string EmptyPassword = "";
                _ = userManager.CreateUser<ApplicationUser>(ObjectSpace, "Admin", EmptyPassword, (user) =>
                {
                    // Add the Administrators role to the user
                    user.Roles.Add(adminRole);
                });
            }

            ObjectSpace.CommitChanges(); //This line persists created object(s).

            SeedNorthwindData();
#endif
        }
        public override void UpdateDatabaseBeforeUpdateSchema()
        {
            base.UpdateDatabaseBeforeUpdateSchema();
        }
        PermissionPolicyRole CreateAdminRole()
        {
            PermissionPolicyRole adminRole = ObjectSpace.FirstOrDefault<PermissionPolicyRole>(r => r.Name == "Administrators");
            if (adminRole == null)
            {
                adminRole = ObjectSpace.CreateObject<PermissionPolicyRole>();
                adminRole.Name = "Administrators";
                adminRole.IsAdministrative = true;
            }
            return adminRole;
        }
        PermissionPolicyRole CreateDefaultRole()
        {
            PermissionPolicyRole defaultRole = ObjectSpace.FirstOrDefault<PermissionPolicyRole>(role => role.Name == "Default");
            if (defaultRole == null)
            {
                defaultRole = ObjectSpace.CreateObject<PermissionPolicyRole>();
                defaultRole.Name = "Default";

                defaultRole.AddObjectPermissionFromLambda<ApplicationUser>(SecurityOperations.Read, cm => cm.ID == (Guid)CurrentUserIdOperator.CurrentUserId(), SecurityPermissionState.Allow);
                defaultRole.AddNavigationPermission(@"Application/NavigationItems/Items/Default/Items/MyDetails", SecurityPermissionState.Allow);
                defaultRole.AddMemberPermissionFromLambda<ApplicationUser>(SecurityOperations.Write, "ChangePasswordOnFirstLogon", cm => cm.ID == (Guid)CurrentUserIdOperator.CurrentUserId(), SecurityPermissionState.Allow);
                defaultRole.AddMemberPermissionFromLambda<ApplicationUser>(SecurityOperations.Write, "StoredPassword", cm => cm.ID == (Guid)CurrentUserIdOperator.CurrentUserId(), SecurityPermissionState.Allow);
                defaultRole.AddTypePermissionsRecursively<PermissionPolicyRole>(SecurityOperations.Read, SecurityPermissionState.Deny);
                defaultRole.AddObjectPermission<ModelDifference>(SecurityOperations.ReadWriteAccess, "UserId = ToStr(CurrentUserId())", SecurityPermissionState.Allow);
                defaultRole.AddObjectPermission<ModelDifferenceAspect>(SecurityOperations.ReadWriteAccess, "Owner.UserId = ToStr(CurrentUserId())", SecurityPermissionState.Allow);
                defaultRole.AddTypePermissionsRecursively<ModelDifference>(SecurityOperations.Create, SecurityPermissionState.Allow);
                defaultRole.AddTypePermissionsRecursively<ModelDifferenceAspect>(SecurityOperations.Create, SecurityPermissionState.Allow);
            }
            return defaultRole;
        }

        void SeedNorthwindData()
        {
            if (ObjectSpace.FirstOrDefault<Customer>(c => c.CompanyName != null) != null)
            {
                return;
            }

            var random = new Random(12345);

            var regions = CreateRegions();
            var territories = CreateTerritories(regions);
            var departments = CreateDepartments();
            var employees = CreateEmployees(territories, departments);
            var categories = CreateCategories();
            var suppliers = CreateSuppliers();
            var products = CreateProducts(categories, suppliers, random);
            var customers = CreateCustomers(random);
            var shippers = CreateShippers(random);
            var orders = CreateOrders(customers, employees, shippers, products, random);
            CreateInvoices(orders, random);

            ObjectSpace.CommitChanges();
        }

        IList<Region> CreateRegions()
        {
            var names = new[] { "North", "South", "East", "West" };
            var regions = new List<Region>();
            foreach (var name in names)
            {
                var region = ObjectSpace.CreateObject<Region>();
                region.Name = name;
                regions.Add(region);
            }
            return regions;
        }

        IList<Territory> CreateTerritories(IList<Region> regions)
        {
            var data = new (string Name, int RegionIndex)[]
            {
                ("Seattle", 0), ("Portland", 0), ("Spokane", 0),
                ("Los Angeles", 1), ("San Diego", 1), ("Phoenix", 1),
                ("Denver", 2), ("Dallas", 2), ("Houston", 2),
                ("Chicago", 3), ("Detroit", 3), ("Minneapolis", 3)
            };

            var territories = new List<Territory>();
            foreach (var item in data)
            {
                var territory = ObjectSpace.CreateObject<Territory>();
                territory.Name = item.Name;
                territory.Region = regions[item.RegionIndex];
                territories.Add(territory);
            }
            return territories;
        }

        IList<Department> CreateDepartments()
        {
            var data = new (string Name, string Code, string Location, decimal Budget, bool IsActive)[]
            {
                ("Sales", "SALES", "Building A, Floor 2", 500000m, true),
                ("Engineering", "ENG", "Building B, Floor 1", 1200000m, true),
                ("Human Resources", "HR", "Building A, Floor 1", 300000m, true),
                ("Marketing", "MKT", "Building C, Floor 3", 450000m, true),
                ("Finance", "FIN", "Building A, Floor 3", 350000m, true)
            };

            var departments = new List<Department>();
            foreach (var item in data)
            {
                var department = ObjectSpace.CreateObject<Department>();
                department.Name = item.Name;
                department.Code = item.Code;
                department.Location = item.Location;
                department.Budget = item.Budget;
                department.IsActive = item.IsActive;
                departments.Add(department);
            }
            return departments;
        }

        IList<Employee> CreateEmployees(IList<Territory> territories, IList<Department> departments)
        {
            var employees = new List<Employee>();
            var data = new[]
            {
                new { First = "Nancy", Last = "Davolio", Title = "Sales Manager", Hire = new DateTime(2020, 1, 5), Email = "nancy@example.com", Phone = "555-0100" },
                new { First = "Andrew", Last = "Fuller", Title = "Senior Sales", Hire = new DateTime(2021, 3, 12), Email = "andrew@example.com", Phone = "555-0101" },
                new { First = "Janet", Last = "Leverling", Title = "Sales Representative", Hire = new DateTime(2022, 5, 20), Email = "janet@example.com", Phone = "555-0102" },
                new { First = "Margaret", Last = "Peacock", Title = "Sales Representative", Hire = new DateTime(2023, 2, 10), Email = "margaret@example.com", Phone = "555-0103" },
                new { First = "Steven", Last = "Buchanan", Title = "Sales Associate", Hire = new DateTime(2024, 7, 1), Email = "steven@example.com", Phone = "555-0104" }
            };

            foreach (var item in data)
            {
                var employee = ObjectSpace.CreateObject<Employee>();
                employee.FirstName = item.First;
                employee.LastName = item.Last;
                employee.Title = item.Title;
                employee.HireDate = item.Hire;
                employee.Email = item.Email;
                employee.Phone = item.Phone;
                employees.Add(employee);
            }

            // Assign managers
            employees[1].ReportsTo = employees[0];
            employees[2].ReportsTo = employees[0];
            employees[3].ReportsTo = employees[1];
            employees[4].ReportsTo = employees[2];

            // Assign departments
            employees[0].Department = departments[0]; // Nancy -> Sales
            employees[1].Department = departments[0]; // Andrew -> Sales
            employees[2].Department = departments[1]; // Janet -> Engineering
            employees[3].Department = departments[1]; // Margaret -> Engineering
            employees[4].Department = departments[3]; // Steven -> Marketing

            // Territory assignments
            var assignments = new Dictionary<int, int[]>
            {
                { 0, new[]{0,1,2} },
                { 1, new[]{3,4} },
                { 2, new[]{5,6} },
                { 3, new[]{7,8} },
                { 4, new[]{9,10,11} }
            };

            foreach (var entry in assignments)
            {
                foreach (var territoryIndex in entry.Value)
                {
                    var link = ObjectSpace.CreateObject<EmployeeTerritory>();
                    link.Employee = employees[entry.Key];
                    link.Territory = territories[territoryIndex];
                }
            }

            return employees;
        }

        IList<Category> CreateCategories()
        {
            var names = new[] { "Beverages", "Condiments", "Confections", "Dairy", "Grains", "Meat", "Produce", "Seafood" };
            var categories = new List<Category>();
            foreach (var name in names)
            {
                var category = ObjectSpace.CreateObject<Category>();
                category.Name = name;
                categories.Add(category);
            }
            return categories;
        }

        IList<Supplier> CreateSuppliers()
        {
            var data = new (string Company, string Contact, string Phone, string City, string Country)[]
            {
                ("Exotic Liquids", "Charlotte Cooper", "(171) 555-2222", "London", "UK"),
                ("New Orleans Cajun Delights", "Shelley Burke", "(100) 555-4822", "New Orleans", "USA"),
                ("Grandma Kelly's Homestead", "Regina Murphy", "(313) 555-5735", "Ann Arbor", "USA"),
                ("Tokyo Traders", "Yoshi Nagase", "(03) 3555-5011", "Tokyo", "Japan"),
                ("Cooperativa de Quesos", "Antonio del Valle", "(98) 598 76 54", "Oviedo", "Spain"),
                ("Mayumi's", "Mayumi Ohno", "(06) 431-7877", "Osaka", "Japan"),
                ("Pavlova", "Ian Devling", "(0261) 155633", "Melbourne", "Australia"),
                ("Nord-Ost", "Sven Petersen", "(047) 555-1212", "Hamburg", "Germany"),
                ("Formaggi Fortini", "Elio Rossi", "(0544) 60323", "Ravenna", "Italy"),
                ("Healthy Kiosk", "Linn Svensson", "(08) 598 42 30", "Stockholm", "Sweden")
            };

            var suppliers = new List<Supplier>();
            foreach (var item in data)
            {
                var supplier = ObjectSpace.CreateObject<Supplier>();
                supplier.CompanyName = item.Company;
                supplier.ContactName = item.Contact;
                supplier.Phone = item.Phone;
                supplier.City = item.City;
                supplier.Country = item.Country;
                suppliers.Add(supplier);
            }
            return suppliers;
        }

        IList<Product> CreateProducts(IList<Category> categories, IList<Supplier> suppliers, Random random)
        {
            var names = new[]
            {
                "Chai", "Chang", "Aniseed Syrup", "Chef Anton's Cajun Seasoning", "Chef Anton's Gumbo Mix",
                "Grandma's Boysenberry Spread", "Uncle Bob's Organic Dried Pears", "Northwoods Cranberry Sauce", "Mishi Kobe Niku", "Ikura",
                "Queso Cabrales", "Queso Manchego", "Konbu", "Tofu", "Genen Shouyu",
                "Pavlova", "Alice Mutton", "Carnarvon Tigers", "Teatime Chocolate Biscuits", "Sir Rodney's Scones",
                "Gustaf's Knäckebröd", "Tunnbröd", "Guaraná Fantástica", "Sasquatch Ale", "Steeleye Stout",
                "Inlagd Sill", "Gravad lax", "Côte de Blaye", "Chartreuse verte", "Ipoh Coffee"
            };

            var products = new List<Product>();
            foreach (var name in names)
            {
                var product = ObjectSpace.CreateObject<Product>();
                product.Name = name;
                product.UnitPrice = Math.Round((decimal)(random.NextDouble() * 248) + 2m, 2);
                product.UnitsInStock = random.Next(10, 120);
                product.Discontinued = random.NextDouble() < 0.05;
                product.Category = categories[random.Next(categories.Count)];
                product.Supplier = suppliers[random.Next(suppliers.Count)];
                products.Add(product);
            }
            return products;
        }

        IList<Customer> CreateCustomers(Random random)
        {
            var data = new (string Company, string Contact, string City, string Country)[]
            {
                ("Alfreds Futterkiste", "Maria Anders", "Berlin", "Germany"),
                ("Ana Trujillo Emparedados", "Ana Trujillo", "México D.F.", "Mexico"),
                ("Antonio Moreno Taquería", "Antonio Moreno", "México D.F.", "Mexico"),
                ("Around the Horn", "Thomas Hardy", "London", "UK"),
                ("Berglunds snabbköp", "Christina Berglund", "Luleå", "Sweden"),
                ("Blauer See Delikatessen", "Hanna Moos", "Mannheim", "Germany"),
                ("Blondel père et fils", "Frédérique Citeaux", "Strasbourg", "France"),
                ("Bólido Comidas", "Martín Sommer", "Madrid", "Spain"),
                ("Bon app'", "Laurence Lebihan", "Marseille", "France"),
                ("Bottom-Dollar Markets", "Elizabeth Lincoln", "Tsawassen", "Canada"),
                ("Cactus Comidas", "Patricio Simpson", "Buenos Aires", "Argentina"),
                ("Centro comercial Moctezuma", "Francisco Chang", "México D.F.", "Mexico"),
                ("Chop-suey Chinese", "Yang Wang", "Bern", "Switzerland"),
                ("Comércio Mineiro", "Pedro Afonso", "São Paulo", "Brazil"),
                ("Consolidated Holdings", "Elizabeth Brown", "London", "UK"),
                ("Drachenblut Delikatessen", "Sven Ottlieb", "Aachen", "Germany"),
                ("Du monde entier", "Janine Labrune", "Paris", "France"),
                ("Eastern Connection", "Ann Devon", "London", "UK"),
                ("Ernst Handel", "Roland Mendel", "Graz", "Austria"),
                ("Familia Arquibaldo", "Aria Cruz", "São Paulo", "Brazil")
            };

            var customers = new List<Customer>();
            foreach (var item in data)
            {
                var customer = ObjectSpace.CreateObject<Customer>();
                customer.CompanyName = item.Company;
                customer.ContactName = item.Contact;
                customer.City = item.City;
                customer.Country = item.Country;
                customer.Phone = $"+1-555-{random.Next(1000, 9999)}";
                customers.Add(customer);
            }
            return customers;
        }

        IList<Shipper> CreateShippers(Random random)
        {
            var names = new[] { "Speedy Express", "United Package", "Federal Shipping" };
            var shippers = new List<Shipper>();
            foreach (var name in names)
            {
                var shipper = ObjectSpace.CreateObject<Shipper>();
                shipper.CompanyName = name;
                shipper.Phone = $"+1-800-{random.Next(1000, 9999)}";
                shippers.Add(shipper);
            }
            return shippers;
        }

        IList<Order> CreateOrders(IList<Customer> customers, IList<Employee> employees, IList<Shipper> shippers, IList<Product> products, Random random)
        {
            var orders = new List<Order>();
            var statuses = Enum.GetValues<OrderStatus>();
            var baseDate = new DateTime(2024, 1, 1);

            for (int i = 0; i < 50; i++)
            {
                var order = ObjectSpace.CreateObject<Order>();
                order.Customer = customers[random.Next(customers.Count)];
                order.Employee = employees[random.Next(employees.Count)];
                order.Shipper = shippers[random.Next(shippers.Count)];
                order.OrderDate = baseDate.AddDays(random.Next(0, 420));
                order.RequiredDate = order.OrderDate.AddDays(7 + random.Next(0, 14));
                order.ShippedDate = random.NextDouble() > 0.2 ? order.OrderDate.AddDays(random.Next(1, 10)) : null;
                order.Freight = Math.Round((decimal)(random.NextDouble() * 95) + 5m, 2);
                order.Status = statuses[random.Next(statuses.Length)];
                order.ShipAddress = "123 Market St";
                order.ShipCity = order.Customer.City;
                order.ShipCountry = order.Customer.Country;

                var itemCount = random.Next(2, 5);
                for (int j = 0; j < itemCount; j++)
                {
                    var product = products[random.Next(products.Count)];
                    var item = ObjectSpace.CreateObject<OrderItem>();
                    item.Order = order;
                    item.Product = product;
                    item.UnitPrice = product.UnitPrice;
                    item.Quantity = random.Next(1, 50);
                    item.Discount = random.Next(0, 16);
                }

                orders.Add(order);
            }

            return orders;
        }

        void CreateInvoices(IList<Order> orders, Random random)
        {
            var invoiceStatuses = Enum.GetValues<InvoiceStatus>();
            var orderQueue = new Queue<Order>(orders.OrderBy(_ => random.Next()));
            int invoiceNumber = 1;

            while (orderQueue.Count > 0 && invoiceNumber <= 20)
            {
                var invoice = ObjectSpace.CreateObject<Invoice>();
                invoice.InvoiceNumber = $"INV-{invoiceNumber:0000}";
                invoice.InvoiceDate = new DateTime(2024, 1, 1).AddDays(random.Next(0, 420));
                invoice.DueDate = invoice.InvoiceDate.AddDays(30);
                invoice.Status = invoiceStatuses[random.Next(invoiceStatuses.Length)];

                var ordersInInvoice = random.Next(2, 4);
                for (int i = 0; i < ordersInInvoice && orderQueue.Count > 0; i++)
                {
                    var order = orderQueue.Dequeue();
                    order.Invoice = invoice;
                    invoice.Orders.Add(order);
                }

                invoiceNumber++;
            }
        }
    }
}
