import React, { Component } from 'react';
import { Dispatch } from "redux";
import { BrowserRouter } from 'react-router-dom';

import api from "src/api";
import configureStore from "src/configureStore";
import queryString from "query-string";
import { Provider, connect, } from "react-redux";

import { ThemeContext, Toast } from "ui";
import ErrorBoundary from "src/components/common/ErrorBoundary";
import NotFoundErrorBoundary from "src/components/common/Error/NotFoundErrorBoundary";
import YandexMetrika from "src/components/common/YandexMetrika";
import Header from "src/components/common/Header";
import EmailNotConfirmedModal from "src/components/notificationModal/EmailNotConfirmedModal";
import Router from "src/Router";

import theme from "src/uiTheme";

import { AccountProblemType } from "src/consts/accountProblemType";
import { AccountState } from "src/redux/account";
import { RootState } from "src/models/reduxState";
import { deviceChangeAction } from "src/actions/device";
import { DeviceType } from "src/consts/deviceType";
import { getDeviceType } from "./utils/getDeviceType";


const store = configureStore();

// Update notifications count each minute
setInterval(() => {
	if(store.getState().account.isAuthenticated) {
		api.notifications.getNotificationsCount(store.getState().notifications.lastTimestamp)(store.dispatch);
	}
}, 60 * 1000);

api.setServerErrorHandler(
	(message) => Toast.push(message ? message : 'Произошла ошибка. Попробуйте перезагрузить страницу.'));

function UlearnApp(): React.ReactElement {
	return (
		<Provider store={ store }>
			<ConnectedUlearnApp/>
		</Provider>
	);
}

interface Props {
	account: AccountState,
	getNotificationsCount: () => void,
	getCurrentUser: () => void,
	getCourses: () => void,
	setDeviceType: (deviceType: DeviceType) => void,
}

interface State {
	initializing: boolean,
	resizeTimeout?: NodeJS.Timeout,
}

class InternalUlearnApp extends Component<Props, State> {
	constructor(props: Props) {
		super(props);
		this.state = {
			initializing: true,
			resizeTimeout: undefined,
		};
	}

	onWindowResize = () => {
		const { resizeTimeout, } = this.state;
		const { setDeviceType, } = this.props;

		const throttleTimeout = 66;

		//resize event can be called rapidly, to prevent performance issue, we throttling event handler
		if(!resizeTimeout) {
			this.setState({
				resizeTimeout: setTimeout(() => {
					this.setState({
						resizeTimeout: undefined,
					});
					setDeviceType(getDeviceType());
				}, throttleTimeout),
			});
		}
	};

	componentDidMount() {
		const { getCurrentUser, getCourses, } = this.props;
		getCurrentUser();
		getCourses();
		this.setState({
			initializing: false
		});
		window.addEventListener("resize", this.onWindowResize);
	}

	componentDidUpdate(prevProps: Props) {
		const { getNotificationsCount, account, } = this.props;

		if(!prevProps.account.isAuthenticated && account.isAuthenticated) {
			getNotificationsCount();
		}
	}

	componentWillUnmount() {
		window.removeEventListener("resize", this.onWindowResize);
	}


	render() {
		const { initializing, } = this.state;
		const { account } = this.props;

		const pathname = window.location.pathname.toLowerCase();
		const params = queryString.parse(window.location.search);
		const isLti = pathname.endsWith('/ltislide') || pathname.endsWith('/acceptedalert') || params.isLti; //TODO remove this flag,that hiding header and nav menu
		const isHeaderVisible = !isLti;

		return (
			<BrowserRouter>
				<ThemeContext.Provider value={ theme }>
					<ErrorBoundary>
						{ isHeaderVisible &&
						<React.Fragment>
							<Header initializing={ initializing }/>
						</React.Fragment>
						}
						<NotFoundErrorBoundary>
							{ !initializing && // Avoiding bug: don't show page while initializing.
							// Otherwise we make two GET requests sequentially.
							// Unfortunately some our GET handlers are not idempotent (i.e. /Admin/CheckNextExerciseForSlide)
							<Router account={ account }/>
							}
						</NotFoundErrorBoundary>
						{ account
						&& this.isEmailNotConfirmed()
						&& <EmailNotConfirmedModal/>
						}
						{ this.renderMetricsIfNotDevelopment() }
					</ErrorBoundary>
				</ThemeContext.Provider>
			</BrowserRouter>
		);
	}

	isEmailNotConfirmed = () => {
		const { account } = this.props;
		return account.isAuthenticated
			&& account.accountProblems.length > 0
			&& account.accountProblems.some(p => p.problemType === AccountProblemType.emailNotConfirmed);
	};

	renderMetricsIfNotDevelopment = () => {
		if(process.env.NODE_ENV === 'development') {
			return null;
		}
		return <YandexMetrika/>;
	};
}

const mapStateToProps = (state: RootState) => {
	return {
		account: state.account,
	};
};

const mapDispatchToProps = (dispatch: Dispatch) => {
	return {
		getCurrentUser: () => api.account.getCurrentUser()(dispatch),
		getCourses: () => api.courses.getCourses()(dispatch),
		getNotificationsCount: () => api.notifications.getNotificationsCount()(dispatch),
		setDeviceType: (deviceType: DeviceType) => dispatch(deviceChangeAction(deviceType)),
	};
};

const ConnectedUlearnApp = connect(mapStateToProps, mapDispatchToProps)(InternalUlearnApp);

export default UlearnApp;
