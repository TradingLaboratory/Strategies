using System;
using System.Collections.Generic;
using TSLab.Script; // для работы с ТС в TSL
using TSLab.Script.GraphPane; //для работы с графикой на панели цены
using TSLab.Script.Handlers; // для работы с индикаторими и обработчиками
using TSLab.Script.Helpers; // помощники
using TSLab.Script.Optimization; // для оптимизации
using TSLab.Script.Realtime; // для реального времени
using TSLab.TraidingLaboratory.Indicators; //ссылка на нашу библиотеку индикаторов
using System.Xml; //для того, чтобы использовать xml файл
using System.Linq; // Без этого работать не будет!!!

namespace TSLab.TraidingLaboratory.Strategies
{
    public class FractalsTrading : IExternalScript
	{
        #region Создаём переменные на уровне класса

        public IPosition LastActivePosition = null; //объявление переменной, хранящей последнюю активную позицию

        #region Параметры торговой системы

        public IntOptimProperty _CandlesForHighFractal = new IntOptimProperty(5, 2, 50, 1); // Период сглаживания
        public IntOptimProperty _CandlesForLowFractal = new IntOptimProperty(5, 2, 50, 1); // Период сглаживания


        // Параметры управления ММ
        public OptimProperty _maxPercentRisk = new OptimProperty(3.7, 1, 4, 0.1); //Оптимизируем параметр - максимальный риск в одной сделке,
        public OptimProperty _maxKontraktSize = new OptimProperty(1, 1, 50, 1);
        public OptimProperty _punktPriceRub = new OptimProperty(1, 1, 10, 0.1);//задаем параметры для оптимизации
        public OptimProperty _absComission = new OptimProperty(5, 0, 10, 1);//задаем параметры для оптимизации
        public BoolOptimProperty _writeToLog = new BoolOptimProperty(true); //3, 0, 10, 1);//задаем параметры для оптимизации


        #endregion

        #region Создаём экземпляры кубиков (класса Handlers)

        private WholeTimeProfit WholeTimeProfit_h = new WholeTimeProfit(); //доход за всё время
        private lots_maxPercentRisk lots_maxPercentRisk_h = new lots_maxPercentRisk(); //создали экземпляр для расчёта maxPercentRisk
        private AbsolutCommission AbsComission_h = new AbsolutCommission(); //создаём экземлпяр класса
                                                                            //  private FractalSellDouble FractalSellDouble_h = new FractalSellDouble(); //создаём экземлпяр класса
                                                                            //  private FractalBuyDouble FractalBuyDouble_h = new FractalBuyDouble(); //создаём экземлпяр класса
        private FractalBuyDouble FractalBuyDouble_h = new FractalBuyDouble();
        private FractalSellDouble FractalSellDouble_h = new FractalSellDouble();


        #endregion

        #endregion

        public virtual void Execute(IContext context, ISecurity symbol)  // IContext ctx - источник данных, ISecurity sec - фин инструмент и инф.о нем
        {

            #region Забираем значения из параметров
            double punktPriceRub = _punktPriceRub.Value; // 2d / 100 * 66; //стоимость пункта по фьючу на РТС
            double maxKontraktSize = _maxKontraktSize.Value; //Указываем максимальное количество контрактов. Защита "От Дурака" на момент отладки
            double maxPercentRisk = _maxPercentRisk.Value;  //максимальный риск в одной сделке,
            double absComission = _absComission.Value;


            int CandlesForHighFractal = _CandlesForHighFractal.Value; //_periodExit.ValueInt; //Определяем период канала
            int CandlesForLowFractal = _CandlesForLowFractal.Value; //_periodExit.ValueInt; //Определяем период канала

            bool writeToLog = _writeToLog.Value; //по умолчанию true
            
            #endregion

            #region Переменные для работы с Агентом

            double extraBalanceMoney = 0; //для режима "Агент" сколько денег временно вывели со счёта
            double countOfTradingSystems = 10; //количество одновременно торгуемых систем
            double currentBalanceForAgent = 0; //Переменная для определения суммы на счёте в ТСЛаб

            #endregion

            #region забираем данные из xml - файла

            string pathXML_TSLab = @"D:\Лаборатория Трейдинга - VisualStudio\Файл конфигурации\configXML.xml";
            //     string pathXML_TSLab = @"C:\TSLab2 - Tools\Файл конфигурации XML\configXML.xml";

            XmlDocument doc = new XmlDocument(); //экземпляр класса для работы с xml документами
            doc.Load(pathXML_TSLab); //загружаем созданный xml - документ

            foreach (XmlNode node in doc.DocumentElement)
            {
                string name = node.Attributes[0].Value;
                if (name == "ALOR_TradingLab") //указываем тот счёт на котором должна вестись торговля
                {
                    extraBalanceMoney = double.Parse(node["extraBalanceMoney"].InnerText);
                    countOfTradingSystems = double.Parse(node["countOfTradingSystems"].InnerText);
                    break;
                }
            }

            #endregion

            #region определяем процент на одну торговую систему

            double pctForSystem = 100.0 / countOfTradingSystems; //указываем максимальный процент денег на одну систему 
            double maxPctForOneSystem = 10.0;  //указываем максимальный процент на одну торговую систему
            pctForSystem = Math.Min(maxPctForOneSystem, pctForSystem);
            pctForSystem = Math.Round(pctForSystem, 2);

            #endregion

            #region Переменные для торговой системы

            int firstValidValue = 0; // Первое значение свечки при которой существуют все индикаторы

            double FinResForBar = 0; //Переменная, в которой указывается фин. результат на текущий бар
            double moneyForTradingSystem = 0; //Переменная, которая будет хранить в себе оценку портфеля


            bool signalBuy = false; //Сигнал на вход в длинную позицию
            double orderEntryLong = 0; // Цена, где будет расположен вход в длинную позицию
            double stopPriceLong = 0; // Цена где будет расположен StopLoss длинной позиции
            double kontraktShort_mPR = 0;

            bool signalShort = false; // Сигналы на вход в короткую позиции
            double orderEntryShort = 0; //Цена, где будет расположен вод в короткую позицию
            double stopPriceShort = 0; // Цена где будет расположен StopLoss длинной позиции
            double kontraktBuy_mPR = 0;

            double exitLimitPrice = 0; //Цена выставления Лимитированной заявки на выход

            string HandlerName = String.Format(""); //текстовая переменная для названия индикатора
            string ErrorText = String.Format(""); //текстовая переменная для вывода текста ошибки


            #endregion

            #region Проводим расчёт Абсолютной комиссии (AbsComission) - не индикатор, а проводим расчёт:

            HandlerName = String.Format("Абсолютная комиссия по {0}", symbol.CacheName);
            ErrorText = String.Format("Ошибка при вычислении блока {0}. Индекс за пределами диапазона.", HandlerName);

            //Устанавливаем размер комиссии
           
            AbsComission_h.Commission = absComission; //устанавливаем комиссию в рублях на контракт
            AbsComission_h.Execute(symbol); //Показываем, что эта комиссия должна учитываться для фин. инструмента

            #endregion

            ISecurityRt rtSymbol = symbol as ISecurityRt;// создаем объект для доступа к информации реальной торговли

            #region Создаём удобное обращение к ценам (по аналогии с Wealth-Lab)

            IList<double> Close = symbol.GetClosePrices(context);
            IList<double> Open = symbol.GetOpenPrices(context);
            IList<double> High = symbol.GetHighPrices(context);//получаем список максимальных цен
            IList<double> Low = symbol.GetLowPrices(context);

            #endregion


            #region Индикаторы

            #region  Верхние фракталы

            FractalBuyDouble_h.Context = context;
            FractalBuyDouble_h.CurrentBar = 0; //появляется когда фрактал полностью сформируется
            FractalBuyDouble_h.Fractal = 0;
            FractalBuyDouble_h.Left = CandlesForHighFractal;
            FractalBuyDouble_h.Right = CandlesForHighFractal;

            HandlerName = String.Format("Верхний фрактал (свечей влево: {0}, свечей вправо: {1}) по инструменту: {2}", CandlesForHighFractal, CandlesForHighFractal, symbol.CacheName);
            ErrorText = String.Format("Ошибка при вычислении блока {0}. Индекс за пределами диапазона.", HandlerName);


            IList<double> highFractal = context.GetData
                (HandlerName, //вводим название нового индикатора
                new[] { HandlerName }, //работа с буфером данных
                delegate // именно здесь расчитывается индикатор
                {
                    try { return FractalBuyDouble_h.Execute(symbol); } //Рассчитываем индикатор
                    catch (ArgumentOutOfRangeException) //если произошла ошибка
                    { throw new ScriptException(ErrorText); } //выводим текст ошибки
                }
                );

            firstValidValue = Math.Max(firstValidValue, CandlesForHighFractal * 2 + 1);

            #endregion

            #region  Нижние фракталы

            FractalSellDouble_h.Context = context;
            FractalSellDouble_h.CurrentBar = 0; //появляется когда фрактал полностью сформируется
            FractalSellDouble_h.Fractal = 0;
            FractalSellDouble_h.Left = CandlesForLowFractal;
            FractalSellDouble_h.Right = CandlesForLowFractal;

            HandlerName = String.Format("Нижний фрактал (свечей влево: {0}, свечей вправо: {1}) по инструменту: {2}", CandlesForLowFractal, CandlesForLowFractal, symbol.CacheName);
            ErrorText = String.Format("Ошибка при вычислении блока {0}. Индекс за пределами диапазона.", HandlerName);


            IList<double> lowFractal = context.GetData
                (HandlerName, //вводим название нового индикатора
                new[] { HandlerName }, //работа с буфером данных
                delegate // именно здесь расчитывается индикатор
                {
                    try { return FractalSellDouble_h.Execute(symbol); } //Рассчитываем индикатор
                    catch (ArgumentOutOfRangeException) //если произошла ошибка
                    { throw new ScriptException(ErrorText); } //выводим текст ошибки
                }
                );

            firstValidValue = Math.Max(firstValidValue, CandlesForHighFractal * 2 + 1);

            #endregion

            #endregion

            #region Пишем сообщение в лог о начале срабатывания метода Execute() - если значение writeLog == true
            if (writeToLog == true)
            {
                if (symbol.IsRealtime)
                {
                    string logText = String.Format
                            ("Привет! Я ТСЛаб. Запускаю стратегию в Режиме Агента. ExtraMoney={0}; торгуется систем: {1}; % для системы = {2}",
                            extraBalanceMoney.ToString(), countOfTradingSystems, pctForSystem);

                    context.Log(logText, new Color(0, 0, 0), true); //выводим сообщение в лог
                }
                else
                {
                    string logText = String.Format
                        ("Привет! Я ТСЛаб. Я начала тестировать стратегию в Режиме Лаборатории. ExtraMoney={0}; торгуется систем: {1}; % для системы = {2}",
                        extraBalanceMoney, countOfTradingSystems, pctForSystem);

                    context.Log(logText, new Color(0, 0, 0), true); //выводим сообщение в лог
                }
            }
            #endregion

            
            #region Главный торговый цикл

            for (int bar = firstValidValue; bar < symbol.Bars.Count; bar++)
            {
                this.LastActivePosition = symbol.Positions.GetLastPositionActive(bar);// получить ссылку на последнию позицию

                #region Определяем цены  лимитных заявок:

                orderEntryLong = Close[bar];
                orderEntryLong = symbol.RoundPrice(orderEntryLong);

                orderEntryShort = Close[bar];
                orderEntryShort = symbol.RoundPrice(orderEntryShort);

                exitLimitPrice = Close[bar];
                exitLimitPrice = symbol.RoundPrice(exitLimitPrice);

                #endregion

                #region Условия на вход в позицию

                signalBuy = Close[bar] > highFractal[bar] && highFractal[bar] > lowFractal[bar];

                signalShort = Close[bar] < lowFractal[bar] && highFractal[bar] > lowFractal[bar];


                #endregion

                #region Сопровождение и выход из позиции

                #region если позиция существует

                if (LastActivePosition != null) // Если позиция есть
                {
                    #region Длинная позиция

                    if (LastActivePosition.IsLong == true)
                    {

                        bool ExitLong = false;

                        ExitLong = Close[bar] < lowFractal[bar]; //signalShort;

                        #region нужно выходить на следующем баре
                        if (ExitLong == true && signalBuy == false)
                        {
                            LastActivePosition.CloseAtPrice(bar + 1, exitLimitPrice, "Exit Long");
                        }
                        #endregion

                 
                    }

                    #endregion

                    #region Короткая позиция

                    else if (LastActivePosition.IsShort)
                    {

                        bool ExitShort = false;
                        ExitShort = Close[bar] > highFractal[bar]; //signalBuy;
     
                        #region нужно выходить на следующем баре

                        if (ExitShort == true && signalShort == false)
                        {
                            LastActivePosition.CloseAtPrice(bar + 1, exitLimitPrice, "Exit Short");
                        }
                        #endregion

                       
                    }

                    #endregion
                }

                #endregion

                #region если позиция отсутствует

                else // Если нет позиции
                {

                    #region Нужно входить в длинную позицию?

                    if (signalBuy) // Пришёл сигнал в длинную позицию
                    {

                        stopPriceLong = lowFractal[bar]; //Math.Min(valueLow_1, valueLow_2); //устанавливаем стоп-лос
                        stopPriceLong = symbol.RoundPrice(stopPriceLong); //округляем цену до минимального тика

                        #region Определяем кол-во контрактов на покупку (в зависимости от режима торговли - Лаборатория или Агент:

                        #region Находимся в режиме Агента?

                        if (symbol.IsRealtime) //если находимся в режиме агента
                        {
                            //определяем текущий баланс счёта (В ТСЛаб)
                            currentBalanceForAgent = rtSymbol.EstimatedBalance;
                            moneyForTradingSystem = (currentBalanceForAgent + extraBalanceMoney) * (pctForSystem / 100.0);

                            lots_maxPercentRisk_h.LotSize = symbol.LotSize; //устанавливаем размер лота
                            lots_maxPercentRisk_h.MaxPercentRisk = maxPercentRisk; //указываем процент риска который используем
                            lots_maxPercentRisk_h.punktPriceRUB = punktPriceRub; //указываем стоимость пункта

                            kontraktBuy_mPR = lots_maxPercentRisk_h.Execute(moneyForTradingSystem, orderEntryLong, stopPriceLong, bar);
                            kontraktBuy_mPR = symbol.RoundShares(kontraktBuy_mPR);

                            kontraktBuy_mPR = Math.Min(kontraktBuy_mPR, maxKontraktSize); //ограничиваем максимальное кол-во контрактов на вход (не более ... контрактов)

                            #region пишем сообщение в лог:

                            if (bar > symbol.Bars.Count - 2)
                            {
                                string logTextBuy = String.Format(
                                    "Хочу войти в long: {0} контрактами: Баланс на счёте: {1}; ExtraMoney: {2} руб.; %для системы: {3}, Деньги для системы: {4}",
                                     kontraktBuy_mPR, currentBalanceForAgent, extraBalanceMoney, pctForSystem, moneyForTradingSystem);

                                context.Log(logTextBuy, MessageType.Info, true); //выводим сообщение в лог

                            }

                            #endregion

                        }

                        #endregion

                        #region Находимся в режиме Лаборатории?

                        else //если находимся в режиме лаборатории
                        {
                            string logTextBuy = "";

                            WholeTimeProfit_h.ProfitKind = ProfitKind.Unfixed; //если хотим узнать стоимость счёта с учётом ещё незафиксированной прибыли
                                                                               //  WholeTimeProfit_h.ProfitKind = ProfitKind.Fixed; //если хотим узнать стоимость счёта без учёта ещё незафиксированной прибыли
                            FinResForBar = WholeTimeProfit_h.Execute(symbol, bar); //Рассчитываем стоимость дохода на текущий бар в пунктах
                            FinResForBar = FinResForBar * punktPriceRub; //переводим финрез в рубли
                            //     определяем сумму для торговой системы
                            moneyForTradingSystem = symbol.InitDeposit + FinResForBar;

                            lots_maxPercentRisk_h.LotSize = symbol.LotSize; //устанавливаем размер лота
                            lots_maxPercentRisk_h.MaxPercentRisk = maxPercentRisk; //указываем процент риска который используем
                            lots_maxPercentRisk_h.punktPriceRUB = punktPriceRub; //указываем стоимость пункта

                            kontraktBuy_mPR = lots_maxPercentRisk_h.Execute(moneyForTradingSystem, orderEntryLong, stopPriceLong, bar);
                            kontraktBuy_mPR = symbol.RoundShares(kontraktBuy_mPR);

                            #region пишем сообщение в лог (если не в режиме оптимизации):

                            if (writeToLog == true)
                            {
                                if (context.IsOptimization == false) //если не в режиме оптимизации
                                {
                                    logTextBuy = String.Format(
                                        "Цена входа: {0} - Stop: {1} Результат по закрытым позициям: {2}; Начальный депозит: {3} руб.; Деньги для системы: {4}. бар №{5} - Хочу войти в long: {6} контрактами: ",
                                         orderEntryLong, stopPriceLong, FinResForBar, symbol.InitDeposit, moneyForTradingSystem, bar, kontraktBuy_mPR);

                                    context.Log(logTextBuy, new Color(0, 0, 0), false); //выводим сообщение в лог

                                }
                            }
                            #endregion


                        }

                        #endregion

                        #endregion

                        #region Входим не менее чем на 1 контракт

                        if (kontraktBuy_mPR > 0)
                        {
                           // kontraktBuy_mPR = 1;
                            string orderText = String.Format("LongEnter");
                            symbol.Positions.BuyAtPrice(bar + 1, kontraktBuy_mPR, orderEntryLong, orderText);
                        }
                        else
                        {
                            kontraktBuy_mPR = 1; //даже если не хватает денег - заходим одним контрактом
                            string orderText = String.Format("LongEnter_minKontrakt");
                            symbol.Positions.BuyAtPrice(bar + 1, 1, kontraktBuy_mPR, orderText); // входим хотя бы 1 контрактом
                        }
                        #endregion


                    }

                    #endregion

                    #region Нужно входить в короткую позицию?

                    else if (signalShort)
                    {
                        // Пришёл сигнал в короткую позицию
                       stopPriceShort = highFractal[bar]; //Math.Max(valueHigh_1, valueHigh_2); //Устанавливаем Стоп (для расчёта кол-ва контрактов)


                        #region Определяем кол-во контрактов на продажу (в зависимости от режима торговли - Лаборатория или Агент:

                        #region Если находимся в режиме агента

                        if (symbol.IsRealtime) //если находимся в режиме агента
                        {
                            //определяем текущий баланс счёта (В ТСЛаб)
                            currentBalanceForAgent = rtSymbol.EstimatedBalance;
                            moneyForTradingSystem = (currentBalanceForAgent + extraBalanceMoney) * (pctForSystem / 100.0);

                            lots_maxPercentRisk_h.LotSize = symbol.LotSize; //устанавливаем размер лота
                            lots_maxPercentRisk_h.MaxPercentRisk = maxPercentRisk; //указываем процент риска который используем
                            kontraktShort_mPR = lots_maxPercentRisk_h.Execute(moneyForTradingSystem, orderEntryShort, stopPriceShort, bar);
                            kontraktShort_mPR = symbol.RoundShares(kontraktShort_mPR);


                            // Защита от дурака (от непредвиденных событий) - только в режими агента
                            kontraktShort_mPR = Math.Min(kontraktShort_mPR, maxKontraktSize); //ограничиваем максимальное кол-во контрактов на вход (не более ... контрактов)

                            #region пишем сообщение в лог:

                            if (bar > symbol.Bars.Count - 2)
                            {
                                string logTextShort = String.Format(
                                    "Хочу войти в short: {0} контрактами: Баланс на счёте: {1}; ExtraMoney: {2} руб.; %для системы: {3}, Деньги для системы: {4}",
                                     kontraktShort_mPR, currentBalanceForAgent, extraBalanceMoney, pctForSystem, moneyForTradingSystem);

                                context.Log(logTextShort, MessageType.Info, true); //выводим сообщение в лог

                            }

                            #endregion

                        }

                        #endregion

                        #region Если находимся в режиме Лаборатории
                        else //если находимся в режиме лаборатории
                        {
                            //определяем прибыль от торговли на текущий момент и показываем, что хотим учесть даже ещё незафиксированную прибыль

                            WholeTimeProfit_h.ProfitKind = ProfitKind.Unfixed; //если хотим узнать стоимость счёта с учётом ещё незафиксированной прибыли
                            FinResForBar = WholeTimeProfit_h.Execute(symbol, bar); //Рассчитываем доход по портфелю на текущий бар в пунктах
                            FinResForBar = FinResForBar * punktPriceRub; //переводим Фин. Рез. в рубли

                            //     определяем сумму для торговой системы
                            moneyForTradingSystem = symbol.InitDeposit + FinResForBar;

                            //расчет с помощью "кубика"

                            lots_maxPercentRisk_h.LotSize = symbol.LotSize; //устанавливаем размер лота
                            lots_maxPercentRisk_h.MaxPercentRisk = maxPercentRisk; //указываем процент риска который используем
                            lots_maxPercentRisk_h.punktPriceRUB = punktPriceRub;
                            kontraktShort_mPR = lots_maxPercentRisk_h.Execute(moneyForTradingSystem, orderEntryShort, stopPriceShort, bar);
                            kontraktShort_mPR = symbol.RoundShares(kontraktShort_mPR);

                            #region пишем сообщение в лог (если не в  режиме оптимизации):

                            if (writeToLog == true)
                            {
                                if (context.IsOptimization == false)
                                {
                                    string logTextShort = "";
                                    logTextShort = String.Format(
                                            "Цена входа: {0} - Stop: {1} Результат по закрытым позициям: {2}; Начальный депозит: {3} руб.; Деньги для системы: {4}. бар №{5} - Хочу войти в short: {6} контрактами: ",
                                             orderEntryShort, stopPriceShort, FinResForBar, symbol.InitDeposit, moneyForTradingSystem, bar, kontraktShort_mPR);

                                    context.Log(logTextShort, new Color(0, 0, 0), false); //выводим сообщение в лог

                                }
                            }
                            #endregion

                        }

                        #endregion

                        #endregion

                        #region Входим не менее чем на 1 контракт

                        if (kontraktShort_mPR > 0)
                        {
                           // kontraktShort_mPR = 1;
                            string orderText = String.Format("ShortEnter");
                            symbol.Positions.SellAtPrice(bar + 1, kontraktShort_mPR, orderEntryShort, orderText);
                        }
                        else
                        {
                            kontraktShort_mPR = 1; //даже если не хватает денег - заходим одним контрактом
                            string orderText = String.Format("ShortEnter_minKontrakt");
                            symbol.Positions.SellAtPrice(bar + 1, kontraktShort_mPR, orderEntryShort, orderText);
                        }
                        #endregion

                    }

                    #endregion
                }

                #endregion

                #endregion

            }

            #endregion
            
            
            #region Прорисовка графиков

            

            if (context.IsOptimization == false) // Если находимся не в режиме оптимизации, то пропускаем отрисовку
            {
                #region Пишем сообщение в лог о завершении стратегии
                if (writeToLog == true)
                {
                    if (symbol.IsRealtime)
                    {
                        string logText = String.Format("Режима агента. Осталось только раскрасить график");
                        context.Log(logText, new Color(0, 0, 0), true); //выводим сообщение в лог
                    }
                    else
                    {
                        string logText = String.Format("Режим Лаборатории: Осталось только раскрасить график!");
                        context.Log(logText, new Color(0, 0, 0), true); //выводим сообщение в лог
                    }
                }
                #endregion

                #region Панель для цен и индикаторов (по ценам)


                #region Создаём панель для цен:

                IGraphPane pricePane = context.CreateGraphPane(symbol.ToString(), null);
                pricePane.Visible = true; //является ли панель видимой?
                pricePane.HideLegend = false; //скрыть (true) или показать (false) легенду
                pricePane.SizePct = 100; //сколько % составляет

                #endregion

                #region создаём переменные для цветов

                Color LightGray = 0xc0c0c0; //Серый цвет
                Color Blue = 0x0000ff;  //Синий цвет
                Color redColor = new Color(255, 0, 0); //создаём красный цвет c помощью RGB (берём значение в Яндексе по запросу "красный цвет код")
                Color greenColor = new Color(50, 205, 50); //создаём зелёный цвет

                #endregion

                #region Заносим бары на панель графика

                IGraphList candlesGraph = pricePane.AddList(symbol.ToString(), symbol, CandleStyles.BAR_CANDLE, LightGray, PaneSides.RIGHT);
                symbol.ConnectSecurityList(candlesGraph); //подключить график к ценной бумаге для обновления в режиме реального времени
                candlesGraph.AlternativeColor = LightGray; //для чисел (к отрицательным) для баров (к медвежьим)
                candlesGraph.Autoscaling = true;
                pricePane.UpdatePrecision(TSLab.Script.PaneSides.RIGHT, symbol.Decimals); //обновить значение на графике

                #endregion

                //Рисуем индикаторы


                #region Свойства индикаторов
                
                string highLevelText = String.Format("Верхняя фрактальная граница канала (слева: {0}, справа: {1})", CandlesForHighFractal, CandlesForHighFractal);
                string lowLevelText = String.Format("Нижняя фрактальная граница канала (слева: {0}, справа: {1})", CandlesForLowFractal, CandlesForLowFractal);
      //          string lowTrailingText = String.Format("LongTrailingStop (период канала: {0}, Количество шагов: {1})", periodExit, steps);
      //          string highTrailingText = String.Format("ShortTrailingStop (период канала: {0}, Количество шагов: {1})", periodExit, steps);

  

                LineStyles highLevelLineStyle = LineStyles.SOLID; //устанавливаем тип линии (по умолчанию Dot) для нижнего канала
                LineStyles lowLevelLineStyle = LineStyles.SOLID; //устанавливаем тип линии (по умолчанию Dot) для нижнего канала
         //       LineStyles lowTrailingLineStyle = LineStyles.DOT; //устанавливаем тип линии (по умолчанию Dot) для нижнего канала
         //       LineStyles highTrailingLineStyle = LineStyles.DOT; //устанавливаем тип линии (по умолчанию Dot) для нижнего канала
 
                ListStyles highLevelListStyle = ListStyles.POINT; //Устанавливаем форму линии (по умолчанию Line) для нижнего канала
                ListStyles lowLevelListStyle = ListStyles.POINT; //Устанавливаем форму линии (по умолчанию Line) для нижнего канала
         //       ListStyles lowTrailingListStyle = ListStyles.LINE_WO_ZERO; //Устанавливаем форму линии (по умолчанию Line) для нижнего канала
         //       ListStyles highTrailingListStyle = ListStyles.LINE_WO_ZERO; //Устанавливаем форму линии (по умолчанию Line) для нижнего канала

                #endregion
                
                #region Наносим индикаторы на график

                IGraphListBase lowLevelGraph = pricePane.AddList("lowFractalLevel", lowLevelText, lowFractal, lowLevelListStyle, redColor, lowLevelLineStyle, PaneSides.RIGHT);
                lowLevelGraph.Thickness = 2; //устанавливаем толщину отображаемой линии
                lowLevelGraph.AlternativeColor = redColor;

                IGraphListBase highLevelGraph = pricePane.AddList("highFractalLevel", highLevelText, highFractal, highLevelListStyle, greenColor, highLevelLineStyle, PaneSides.RIGHT);
                highLevelGraph.Thickness = 1; //устанавливаем толщину отображаемой линии
                highLevelGraph.AlternativeColor = greenColor;

                //IGraphListBase lowTrailingGraph = pricePane.AddList("TralingForLong", lowTrailingText, TrailingForLong, lowTrailingListStyle, redColor, lowTrailingLineStyle, PaneSides.RIGHT);
                //lowTrailingGraph.Thickness = 2; //устанавливаем толщину отображаемой линии
                //lowTrailingGraph.AlternativeColor = redColor;

                //IGraphListBase highTrailingGraph = pricePane.AddList("TrailingForShort", highTrailingText, TrailingForShort, highTrailingListStyle, redColor, highTrailingLineStyle, PaneSides.RIGHT);
                //highTrailingGraph.Thickness = 2; //устанавливаем толщину отображаемой линии
                //highTrailingGraph.AlternativeColor = redColor;
                


                #endregion


                #endregion

                #region Расскрашиваем свечи в цвета в зависимости от наличия и типа позиции
                for (int i = 0; i < context.BarsCount; i++)
                {
                    //Расскрашиваем свечи в цвета, если в позиции
                    var ActivePositionsList = symbol.Positions.GetActiveForBar(i);

                    if (ActivePositionsList.Any()) //если есть хотя бы одна позиция 
                    {
                        if (ActivePositionsList.First().IsLong) //ElementAt(0).IsLong)
                        {
                            candlesGraph.SetColor(i, greenColor); //Зелёный цвет если в длинной позиции
                        }
                        else
                        {
                            candlesGraph.SetColor(i, redColor); //Красный цвет - если в короткой позиции
                        }


                    }
                    else
                    {
                        //  lst.SetColor(i, LightGray); //Серый цвет если позиции нет

                    }
                }
                #endregion

            }



            #endregion

            #region Пишем сообщение в лог о завершении стратегии
            if (writeToLog == true)
            {
                if (symbol.IsRealtime)
                {
                    string logText = String.Format("Режима агента. Стратегия выполнена. Жду дальнейших указаний!");
                    context.Log(logText, new Color(0, 0, 0), true); //выводим сообщение в лог
                }
                else
                {
                    string logText = String.Format("Режим Лаборатории: Стратегия выполнена - можно отдохнуть!");
                    context.Log(logText, new Color(0, 0, 0), true); //выводим сообщение в лог
                }
            }
            #endregion
            
        }

    }
}
