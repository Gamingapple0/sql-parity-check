using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.RDS;
using Constructs;

// The two hops of the migration, one stack each, created for one session and then destroyed.
//   Hop 1: self-managed SQL Server (a local container) -> Amazon RDS for SQL Server Express
//   Hop 2: RDS for SQL Server -> Aurora PostgreSQL with Babelfish (T-SQL on 1433, PostgreSQL on 5432)
// Demo networking on purpose: default VPC, public endpoints, open only to one IP.
//   cdk diff   --all -c myIp=203.0.113.7/32
//   cdk deploy --all -c myIp=203.0.113.7/32
//   cdk destroy --all -c myIp=203.0.113.7/32
var app = new App();
var env = new Amazon.CDK.Environment
{
    Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
    Region = "ap-southeast-2",
};
new Hop1SqlServerStack(app, "Hop1SqlServer", new StackProps { Env = env });
new Hop2BabelfishStack(app, "Hop2Babelfish", new StackProps { Env = env });
app.Synth();

static class Demo
{
    // Password generated into Secrets Manager, without characters that break a connection string.
    public static Credentials Login() =>
        Credentials.FromGeneratedSecret("parity", new CredentialsBaseOptions { ExcludeCharacters = " ;='\"@/\\:{}" });

    public static (IVpc Vpc, SecurityGroup Sg) Network(Stack stack, params int[] ports)
    {
        var myIp = (string?)stack.Node.TryGetContext("myIp")
            ?? throw new ArgumentException("Pass your public IP: -c myIp=<ip>/32");
        var vpc = Vpc.FromLookup(stack, "Default", new VpcLookupOptions { IsDefault = true });
        var sg = new SecurityGroup(stack, "Db", new SecurityGroupProps { Vpc = vpc, AllowAllOutbound = false });
        foreach (var port in ports) sg.AddIngressRule(Peer.Ipv4(myIp), Port.Tcp(port), $"Port {port} from my IP only");
        return (vpc, sg);
    }
}

sealed class Hop1SqlServerStack : Stack
{
    public Hop1SqlServerStack(Construct scope, string id, IStackProps props) : base(scope, id, props)
    {
        var (vpc, sg) = Demo.Network(this, 1433);
        var db = new DatabaseInstance(this, "Target", new DatabaseInstanceProps
        {
            // Express: license included, smallest class.
            Engine = DatabaseInstanceEngine.SqlServerEx(new SqlServerExInstanceEngineProps { Version = SqlServerEngineVersion.VER_16_00_4236_2_V1 }),
            InstanceType = Amazon.CDK.AWS.EC2.InstanceType.Of(InstanceClass.T3, InstanceSize.MICRO),
            AllocatedStorage = 20,
            MultiAz = false,
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC },
            PubliclyAccessible = true,
            SecurityGroups = [sg],
            Credentials = Demo.Login(),
            BackupRetention = Duration.Days(0),
            DeleteAutomatedBackups = true,
            DeletionProtection = false,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });
        new CfnOutput(this, "Endpoint", new CfnOutputProps { Value = db.DbInstanceEndpointAddress });
        new CfnOutput(this, "SecretArn", new CfnOutputProps { Value = db.Secret!.SecretArn });
    }
}

sealed class Hop2BabelfishStack : Stack
{
    public Hop2BabelfishStack(Construct scope, string id, IStackProps props) : base(scope, id, props)
    {
        var (vpc, sg) = Demo.Network(this, 1433, 5432);
        var engine = DatabaseClusterEngine.AuroraPostgres(new AuroraPostgresClusterEngineProps { Version = AuroraPostgresEngineVersion.VER_16_13 });
        var cluster = new DatabaseCluster(this, "Target", new DatabaseClusterProps
        {
            Engine = engine,
            ParameterGroup = new ParameterGroup(this, "Babelfish", new ParameterGroupProps
            {
                Engine = engine,
                Parameters = new Dictionary<string, string>
                {
                    ["rds.babelfish_status"] = "on",
                    // One user database whose dbo schema is PostgreSQL schema "dbo", as in the local rehearsal.
                    // Can't be changed after creation; the default from version 16 is multi-db.
                    ["babelfishpg_tsql.migration_mode"] = "single-db",
                },
            }),
            Writer = ClusterInstance.ServerlessV2("Writer", new ServerlessV2ClusterInstanceProps { PubliclyAccessible = true }),
            ServerlessV2MinCapacity = 0.5,
            ServerlessV2MaxCapacity = 1,
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PUBLIC },
            SecurityGroups = [sg],
            Credentials = Demo.Login(),
            StorageEncrypted = true,
            Backup = new BackupProps { Retention = Duration.Days(1) }, // Aurora's minimum
            DeletionProtection = false,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });
        new CfnOutput(this, "Endpoint", new CfnOutputProps { Value = cluster.ClusterEndpoint.Hostname });
        new CfnOutput(this, "SecretArn", new CfnOutputProps { Value = cluster.Secret!.SecretArn });
    }
}
