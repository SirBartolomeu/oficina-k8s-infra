resource "aws_security_group" "webhook_forwarder" {
  name        = "${var.name_prefix}-webhook-forwarder"
  description = "Lambda privada que encaminha webhooks assinados ao Billing no K3s"
  vpc_id      = local.data_outputs.vpc_id
  ingress     = []
  egress      = []
  tags        = { Name = "${var.name_prefix}-webhook-forwarder" }

  # Egress is managed by aws_vpc_security_group_egress_rule to avoid rule drift.
  lifecycle {
    ignore_changes = [egress]
  }
}

resource "aws_vpc_security_group_egress_rule" "webhook_forwarder_to_billing" {
  security_group_id            = aws_security_group.webhook_forwarder.id
  referenced_security_group_id = local.data_outputs.k3s_security_group_id
  ip_protocol                  = "tcp"
  from_port                    = 30081
  to_port                      = 30081
  description                  = "Forward Mercado Pago webhook to private Billing NodePort"
}

resource "aws_vpc_security_group_ingress_rule" "billing_from_webhook_forwarder" {
  security_group_id            = local.data_outputs.k3s_security_group_id
  referenced_security_group_id = aws_security_group.webhook_forwarder.id
  ip_protocol                  = "tcp"
  from_port                    = 30081
  to_port                      = 30081
  description                  = "Billing webhook only from private API Gateway forwarder Lambda"
}

data "aws_iam_policy_document" "webhook_forwarder_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "webhook_forwarder" {
  name               = "${var.name_prefix}-webhook-forwarder"
  assume_role_policy = data.aws_iam_policy_document.webhook_forwarder_assume.json
}

resource "aws_cloudwatch_log_group" "webhook_forwarder" {
  name              = "/aws/lambda/${var.name_prefix}-webhook-forwarder"
  retention_in_days = 1
}

data "aws_iam_policy_document" "webhook_forwarder" {
  statement {
    actions   = ["logs:CreateLogStream", "logs:PutLogEvents"]
    resources = ["${aws_cloudwatch_log_group.webhook_forwarder.arn}:*"]
  }

  statement {
    actions = [
      "ec2:CreateNetworkInterface",
      "ec2:DescribeNetworkInterfaces",
      "ec2:DescribeSubnets",
      "ec2:DeleteNetworkInterface",
      "ec2:AssignPrivateIpAddresses",
      "ec2:UnassignPrivateIpAddresses"
    ]
    resources = ["*"]
  }
}

resource "aws_iam_role_policy" "webhook_forwarder" {
  name   = "${var.name_prefix}-webhook-forwarder"
  role   = aws_iam_role.webhook_forwarder.id
  policy = data.aws_iam_policy_document.webhook_forwarder.json
}

resource "aws_lambda_function" "webhook_forwarder" {
  function_name    = "${var.name_prefix}-webhook-forwarder"
  role             = aws_iam_role.webhook_forwarder.arn
  runtime          = "dotnet10"
  handler          = "Oficina.WebhookForwarder::Oficina.WebhookForwarder.Function::FunctionHandler"
  filename         = "${path.module}/artifacts/oficina-webhook-forwarder.zip"
  source_code_hash = filebase64sha256("${path.module}/artifacts/oficina-webhook-forwarder.zip")
  timeout          = 12
  memory_size      = 128

  vpc_config {
    subnet_ids         = values(local.data_outputs.private_subnet_ids)
    security_group_ids = [aws_security_group.webhook_forwarder.id]
  }

  environment {
    variables = {
      BILLING_WEBHOOK_URL = "http://${aws_instance.k3s.private_ip}:30081/webhooks/mercado-pago/orders"
    }
  }

  depends_on = [aws_iam_role_policy.webhook_forwarder, aws_cloudwatch_log_group.webhook_forwarder]
}

resource "aws_apigatewayv2_integration" "mercado_pago_webhook" {
  api_id                 = aws_apigatewayv2_api.auth.id
  integration_type       = "AWS_PROXY"
  integration_uri        = aws_lambda_function.webhook_forwarder.invoke_arn
  payload_format_version = "2.0"
}

resource "aws_apigatewayv2_route" "mercado_pago_webhook" {
  api_id    = aws_apigatewayv2_api.auth.id
  route_key = "POST /webhooks/mercado-pago/orders"
  target    = "integrations/${aws_apigatewayv2_integration.mercado_pago_webhook.id}"
}

resource "aws_lambda_permission" "webhook_gateway" {
  statement_id  = "AllowMercadoPagoWebhookHttpApi"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.webhook_forwarder.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.auth.execution_arn}/*/POST/webhooks/mercado-pago/orders"
}
